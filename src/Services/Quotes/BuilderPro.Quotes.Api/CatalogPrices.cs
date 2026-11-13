using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Quotes.Api;

/// <summary>A product as the Catalog service lists it: the ONLY source of a unit price.</summary>
public sealed record CatalogProduct(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("listPrice")] decimal ListPrice,
    [property: JsonPropertyName("currency")] string Currency);

/// <summary>The caller's tenant's products by SKU, or null when the catalog couldn't be read.</summary>
public interface ICatalogPrices
{
    Task<IReadOnlyDictionary<string, CatalogProduct>?> GetAsync(HttpContext http, CancellationToken ct);
}

/// <summary>
/// Reads list prices from the Catalog service with the CALLER'S OWN token, so the catalog applies
/// its own tenant isolation: Quotes can only ever price with products the caller's tenant sells.
/// Any failure returns null and the request fails closed (503); we never price without the catalog.
/// </summary>
public sealed class CatalogPriceClient(HttpClient http, ILogger<CatalogPriceClient> log) : ICatalogPrices
{
    public async Task<IReadOnlyDictionary<string, CatalogProduct>?> GetAsync(HttpContext context, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers[HeaderNames.Authorization], out var auth) || auth.Parameter is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Parameter);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Catalog answered {Status}", (int)response.StatusCode);
                return null;
            }
            var products = await response.Content.ReadFromJsonAsync<CatalogProduct[]>(ct) ?? [];
            return products.ToDictionary(p => p.Sku, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                       or System.Text.Json.JsonException or ArgumentException)
        {
            log.LogWarning(ex, "Reading catalog prices failed");
            return null;
        }
    }
}
