using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace BuilderPro.Assistant.Api;

/// <summary>A countertop, island or backsplash rectangle as the customer says it: feet + inches wide, inches deep (or high).</summary>
public sealed record SectionFeetInches(
    [property: Description("Whole feet of the width, e.g. 10 for 10 ft 6 in")] decimal WidthFt,
    [property: Description("Extra inches of the width, 0-11, e.g. 6 for 10 ft 6 in")] decimal WidthIn,
    [property: Description("Depth in inches (backsplash: height in inches). Standard countertop depth is 25.")] decimal DepthIn);

public sealed record RoomFeet(decimal LengthFt, decimal WidthFt);

/// <summary>What the customer measured. Fill only the part for the product's measure kind (from get_project_steps).</summary>
public sealed record DraftMeasurements(
    [property: Description("measure = countertop: straight, l, l-island, u or u-island")] string? Layout = null,
    [property: Description("measure = countertop: countertop sections")] SectionFeetInches[]? Countertop = null,
    [property: Description("measure = countertop: island or peninsula sections (depth in inches)")] SectionFeetInches[]? Island = null,
    [property: Description("measure = countertop: backsplash sections (DepthIn = height)")] SectionFeetInches[]? Backsplash = null,
    [property: Description("measure = roof: footprint in sq ft seen from above")] decimal? RoofFootprintSqFt = null,
    [property: Description("measure = roof: pitch rise in inches per 12 (6 for a 6/12 roof)")] decimal? RoofPitchRise = null,
    [property: Description("measure = roof: eaves + rakes length in ft, 0 if unknown")] decimal? RoofEavesAndRakesFt = null,
    [property: Description("measure = area: rooms in feet")] RoomFeet[]? Rooms = null,
    [property: Description("measure = count: how many")] decimal? Count = null);

/// <summary>One answer to an option step: Sku null = the step's "none" choice. Quantity only for quantity steps.</summary>
public sealed record DraftChoice(string Group, string? Sku, decimal? Quantity = null);

/// <summary>The latest draft the concierge priced this turn: the guided request (answers only) and its priced preview.</summary>
public sealed class DraftLedger
{
    public JsonElement? Request { get; private set; }
    public JsonElement? Quote { get; private set; }

    public void Priced(JsonElement request, JsonElement quote)
    {
        Request = request;
        Quote = quote;
    }
}

/// <summary>
/// The concierge's tools (3b). They read the catalog for a ZIP and PRICE a draft guided quote
/// (POST /api/quotes/guided/preview). Nothing is saved: the customer saves the draft themselves.
/// Feet + inches are converted to inches here, in code, so the model never does arithmetic.
/// Every result goes through the turn's PriceLedger first (the price guard).
/// </summary>
public sealed class ConciergeTools(IPlatformApi api, PriceLedger ledger, DraftLedger drafts)
{
    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(CheckZipAsync, "check_zip"),
        AIFunctionFactory.Create(ListProjectTypesAsync, "list_project_types"),
        AIFunctionFactory.Create(FindProductsAsync, "find_products"),
        AIFunctionFactory.Create(GetProjectStepsAsync, "get_project_steps"),
        AIFunctionFactory.Create(PriceDraftAsync, "price_draft"),
    ];

    [Description("Checks whether the store serves a project ZIP / postal code. Returns {zip, served}. Always the first step.")]
    public async Task<JsonElement> CheckZipAsync(string zip, CancellationToken ct = default) =>
        Record("check_zip", await api.GetAsync($"/api/catalog/service-area?zip={Uri.EscapeDataString(zip)}", ct));

    [Description("The project types (categories) the store sells in this ZIP, with how many products each.")]
    public async Task<JsonElement> ListProjectTypesAsync(string zip, CancellationToken ct = default)
    {
        var products = await api.GetAsync($"/api/catalog/products?zip={Uri.EscapeDataString(zip)}", ct);
        if (products.ValueKind != JsonValueKind.Array) return Record("list_project_types", products);
        var categories = await api.GetAsync("/api/catalog/categories", ct);
        var names = categories.ValueKind == JsonValueKind.Array
            ? categories.EnumerateArray().Where(c => c.TryGetProperty("scene", out var s) && s.GetString() != "none")
                .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("name").GetString()!)
            : [];
        var types = products.EnumerateArray()
            .GroupBy(p => p.GetProperty("category").GetString()!)
            .Where(g => names.ContainsKey(g.Key))
            .Select(g => new { category = g.Key, name = names[g.Key], products = g.Count() });
        return Record("list_project_types", JsonSerializer.SerializeToElement(types));
    }

    [Description("Products of one project type sold in this ZIP, at this ZIP's price per unit (before tax). Optional words narrow by name.")]
    public async Task<JsonElement> FindProductsAsync(
        string zip,
        [Description("Category id from list_project_types, e.g. countertops")] string category,
        [Description("Words in the product name, e.g. quartz white. Omit for all.")] string? query = null,
        CancellationToken ct = default)
    {
        var all = await api.GetAsync($"/api/catalog/products?zip={Uri.EscapeDataString(zip)}&category={Uri.EscapeDataString(category)}", ct);
        if (all.ValueKind != JsonValueKind.Array) return Record("find_products", all);
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = all.EnumerateArray()
            .Where(p => words.All(w => p.GetProperty("name").GetString()!.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(10)
            .Select(p => new
            {
                sku = p.GetProperty("sku").GetString(),
                name = p.GetProperty("name").GetString(),
                unit = p.GetProperty("unit").GetString(),
                listPrice = p.GetProperty("listPrice").GetDecimal(),
                currency = p.GetProperty("currency").GetString(),
                attributes = p.TryGetProperty("attributes", out var a) ? a : default,
            });
        return Record("find_products", JsonSerializer.SerializeToElement(hits));
    }

    [Description("What to ask for one product in this ZIP: its measure kind (countertop, roof, area or count), the store's minimum, and the option steps (id, label, single or quantity, required, none label, options with sku, name and price).")]
    public async Task<JsonElement> GetProjectStepsAsync(string zip, string sku, CancellationToken ct = default) =>
        Record("get_project_steps", await api.GetAsync($"/api/catalog/products/{Uri.EscapeDataString(sku)}/options?zip={Uri.EscapeDataString(zip)}", ct));

    [Description("Prices the draft quote with the store's pricing engine: measured quantities, grouped lines, tax, total and which required steps are still missing. Call it as soon as you have product + measurements, and again after every answer. It does NOT save anything.")]
    public async Task<JsonElement> PriceDraftAsync(
        string zip, string sku, DraftMeasurements measurements,
        [Description("Answers so far, one per single step (sku or null for its none choice); quantity steps: one per option with its count.")] DraftChoice[]? choices = null,
        CancellationToken ct = default)
    {
        var request = new
        {
            zip,
            sku,
            measurements = ToMeasurements(measurements),
            choices = (choices ?? []).Select(c => new { group = c.Group, sku = c.Sku, quantity = c.Quantity }).ToArray(),
        };
        var result = await api.PostAsync("/api/quotes/guided/preview", request, ct);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("total", out _))
        {
            drafts.Priced(JsonSerializer.SerializeToElement(request), result);
        }
        return Record("price_draft", result);
    }

    /// <summary>Feet + inches -> inches, and the request shape of POST /api/quotes/guided (MeasurementInput).</summary>
    internal static object ToMeasurements(DraftMeasurements m)
    {
        static object[] Sections(SectionFeetInches[]? s) =>
            [.. (s ?? []).Select(x => new { widthIn = x.WidthFt * 12 + x.WidthIn, depthIn = x.DepthIn })];

        return new
        {
            countertop = m.Countertop is null && m.Island is null ? null : new
            {
                layout = m.Layout,
                countertop = Sections(m.Countertop),
                island = Sections(m.Island),
                backsplash = Sections(m.Backsplash),
            },
            roof = m.RoofFootprintSqFt is null ? null : new
            {
                footprintSqFt = m.RoofFootprintSqFt,
                pitchRise = m.RoofPitchRise ?? 0,
                wastePercent = 10,
                eavesAndRakesFt = m.RoofEavesAndRakesFt ?? 0,
            },
            rooms = m.Rooms?.Select(r => new { lengthFt = r.LengthFt, widthFt = r.WidthFt }).ToArray(),
            count = m.Count,
        };
    }

    private JsonElement Record(string tool, JsonElement result)
    {
        ledger.Record(tool, result);
        return result;
    }
}
