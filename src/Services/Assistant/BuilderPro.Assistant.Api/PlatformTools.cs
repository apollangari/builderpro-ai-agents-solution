using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuilderPro.Security;
using Microsoft.Extensions.AI;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Assistant.Api;

/// <summary>The platform APIs the agent's tools use. One implementation calls the real services; tests fake it.</summary>
public interface IPlatformApi
{
    Task<JsonElement> GetAsync(string path, CancellationToken ct);
    Task<JsonElement> PostAsync(string path, object body, CancellationToken ct);
    Task<JsonElement> PutAsync(string path, object body, CancellationToken ct);
}

/// <summary>
/// Calls Catalog and Quotes through service discovery with the CALLER'S OWN bearer token (and store
/// pick), so every tool sees exactly what the signed-in customer may see: same tenant isolation,
/// same scopes. The agent has no credentials of its own.
/// </summary>
public sealed class HttpPlatformApi(IHttpClientFactory factory, IHttpContextAccessor accessor, ITokenExchange exchange) : IPlatformApi
{
    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);

    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) }, ct);

    public Task<JsonElement> PutAsync(string path, object body, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) }, ct);

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var http = accessor.HttpContext ?? throw new InvalidOperationException("No caller.");
        if (AuthenticationHeaderValue.TryParse(http.Request.Headers[HeaderNames.Authorization], out var auth) && auth.Parameter is not null)
        {
            if (exchange.Enabled)
            {
                // On-behalf-of (3c): a token for the platform API for the same person; the caller's own token stays here.
                var obo = await exchange.ForPlatformAsync(auth.Parameter, ct);
                if (obo is null) return JsonSerializer.SerializeToElement(new { error = 401, detail = "token_exchange_failed" });
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", obo);
            }
            else request.Headers.Authorization = auth;   // development fallback only (passthrough)
        }
        var pick = http.Request.Headers[TenantHint.Header].ToString();
        if (ClaimsNormalizer.IsIdentifier(pick)) request.Headers.Add(TenantHint.Header, pick);
        // Every call an agent's tool makes is marked: commit actions (accept, pay, book) refuse it (2n-f).
        request.Headers.Add(AgentCall.Header, "assistant");

        var client = factory.CreateClient(request.RequestUri!.OriginalString.StartsWith("/api/catalog", StringComparison.Ordinal) ? "catalog" : "quotes");
        using var response = await client.SendAsync(request, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        // Errors go back to the model as data (e.g. {"error": 400, ...}) so it can explain them.
        return response.IsSuccessStatusCode ? json : JsonSerializer.SerializeToElement(new { error = (int)response.StatusCode, detail = json });
    }
}

/// <summary>
/// The agent's tools (3a). Read-only: they look things up and price things; nothing is created.
/// Every result is recorded in the turn's <see cref="PriceLedger"/> before the model sees it.
/// </summary>
public sealed class PlatformTools(IPlatformApi api, PriceLedger ledger)
{
    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(ListCategoriesAsync, "list_categories"),
        AIFunctionFactory.Create(SearchProductsAsync, "search_products"),
        AIFunctionFactory.Create(EstimateRoofAsync, "estimate_roof"),
        AIFunctionFactory.Create(PriceProductsAsync, "price_products"),
    ];

    [Description("Lists the product categories and the attributes that describe each one (material, colour, style ...).")]
    public async Task<JsonElement> ListCategoriesAsync(CancellationToken ct = default) =>
        Record("list_categories", await api.GetAsync("/api/catalog/categories", ct));

    [Description("Finds products this store sells. Returns SKU, name, unit, list price, currency and attributes. Optionally filter by category id and/or words in the name.")]
    public async Task<JsonElement> SearchProductsAsync(
        [Description("Category id from list_categories, e.g. countertops, roofing, sheds. Omit for all.")] string? category = null,
        [Description("Words to look for in the product name or SKU, e.g. quartz, white, cedar. Omit for all.")] string? query = null,
        CancellationToken ct = default)
    {
        var path = category is null ? "/api/catalog/products" : $"/api/catalog/products?category={Uri.EscapeDataString(category)}";
        var all = await api.GetAsync(path, ct);
        if (all.ValueKind != JsonValueKind.Array) return Record("search_products", all);

        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = all.EnumerateArray()
            .Where(p => words.All(w => p.GetProperty("name").GetString()!.Contains(w, StringComparison.OrdinalIgnoreCase)
                                    || p.GetProperty("sku").GetString()!.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(15)
            .ToArray();
        return Record("search_products", JsonSerializer.SerializeToElement(hits));
    }

    [Description("Roof take-off: from the roof footprint (sq ft seen from above), pitch rise (inches per 12) and optional waste % and eave+rake length (ft), returns squares, shingle bundles, underlayment rolls and drip edge pieces. Quantities only, no prices.")]
    public async Task<JsonElement> EstimateRoofAsync(decimal footprintSqFt, decimal pitchRise, decimal wastePercent = 10, decimal eavesAndRakesFt = 0, CancellationToken ct = default) =>
        Record("estimate_roof", await api.PostAsync("/api/quotes/estimators/roof", new { footprintSqFt, pitchRise, wastePercent, eavesAndRakesFt }, ct));

    [Description("Prices products at given quantities, each on its own, with this store's list prices and tax. Use this for ANY price or total you mention; never calculate money yourself. Up to 6 items.")]
    public async Task<JsonElement> PriceProductsAsync(
        [Description("SKUs and quantities, e.g. [{\"sku\":\"CT-QTZ-CALACATTA\",\"quantity\":30}]")] PriceItem[] items,
        CancellationToken ct = default) =>
        Record("price_products", await api.PostAsync("/api/quotes/estimates", new { items }, ct));

    private JsonElement Record(string tool, JsonElement result)
    {
        ledger.Record(tool, result);
        return result;
    }
}

public sealed record PriceItem(string Sku, decimal Quantity);
