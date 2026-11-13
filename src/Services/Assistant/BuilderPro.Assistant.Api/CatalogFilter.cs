using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace BuilderPro.Assistant.Api;

/// <summary>Where the customer is browsing: one project type in one ZIP (the product screen).</summary>
public sealed record CatalogContext(string Zip, string Category);

/// <summary>The filter the concierge applied, and every product it matches (the page shows exactly these).</summary>
public sealed record CatalogFilterDto(
    IReadOnlyDictionary<string, string> Attributes, decimal? MinPrice, decimal? MaxPrice, string? Words, IReadOnlyList<string> Skus);

/// <summary>
/// Catalog mode (owner request): the customer narrows a type's products by asking ("white quartz
/// under $90"). The model only chooses the filter; the SERVER applies it to the ZIP's products, and
/// the page shows exactly the matching SKUs. Nothing is saved or started.
/// </summary>
public sealed class CatalogFilterTools(IPlatformApi api, PriceLedger ledger, CatalogContext context)
{
    public CatalogFilterDto? Filter { get; private set; }

    public IList<AITool> All() => [AIFunctionFactory.Create(FilterCatalogAsync, "filter_catalog")];

    [Description("Filters this project type's products sold in the customer's ZIP and shows the matches beside the chat. " +
                 "Call it whenever the customer describes what they want; call it with no arguments to show everything again. " +
                 "Returns how many match and up to 12 of them with name, price per unit (before tax) and attributes.")]
    public async Task<JsonElement> FilterCatalogAsync(
        [Description("Attribute filters by key from the category's attributes, e.g. {\"material\":\"quartz\",\"color\":\"white\"}. A value matches when the product's value contains it (case-insensitive).")]
        Dictionary<string, string>? attributes = null,
        [Description("Lowest price per unit, if the customer gave one.")] decimal? minPrice = null,
        [Description("Highest price per unit, if the customer gave one (\"under $90\" -> 90).")] decimal? maxPrice = null,
        [Description("Words that must appear in the product name, e.g. calacatta.")] string? words = null,
        CancellationToken ct = default)
    {
        var all = await api.GetAsync($"/api/catalog/products?zip={Uri.EscapeDataString(context.Zip)}&category={Uri.EscapeDataString(context.Category)}", ct);
        if (all.ValueKind != JsonValueKind.Array)
        {
            ledger.Record("filter_catalog", all);
            return all;
        }

        var attrs = (attributes ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .ToDictionary(a => a.Key.Trim(), a => a.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var wordList = (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = all.EnumerateArray().Where(p => Matches(p, attrs, minPrice, maxPrice, wordList)).ToArray();

        Filter = new CatalogFilterDto(attrs, minPrice, maxPrice, string.IsNullOrWhiteSpace(words) ? null : words.Trim(),
            [.. matches.Select(p => p.GetProperty("sku").GetString()!)]);
        // The customer's own bounds ("under $90") may be repeated; they count as backed amounts.
        ledger.RecordContext(JsonSerializer.SerializeToElement(new { minPrice, maxPrice }));

        var result = JsonSerializer.SerializeToElement(new
        {
            count = matches.Length,
            products = matches.Take(12).Select(p => new
            {
                sku = p.GetProperty("sku").GetString(),
                name = p.GetProperty("name").GetString(),
                listPrice = p.GetProperty("listPrice").GetDecimal(),
                unit = p.GetProperty("unit").GetString(),
                attributes = p.TryGetProperty("attributes", out var a) ? a : default,
            }),
        });
        ledger.Record("filter_catalog", result);
        return result;
    }

    internal static bool Matches(JsonElement p, IReadOnlyDictionary<string, string> attrs, decimal? min, decimal? max, string[] words)
    {
        var price = p.GetProperty("listPrice").GetDecimal();
        if (min is decimal lo && price < lo) return false;
        if (max is decimal hi && price > hi) return false;
        var name = p.GetProperty("name").GetString() ?? "";
        if (!words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return false;
        if (attrs.Count == 0) return true;
        if (!p.TryGetProperty("attributes", out var pa) || pa.ValueKind != JsonValueKind.Object) return false;
        foreach (var (key, wanted) in attrs)
        {
            var value = pa.EnumerateObject().FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            if (value.ValueKind != JsonValueKind.String) return false;
            var v = value.GetString()!.Replace('-', ' ');
            if (!v.Contains(wanted.Replace('-', ' '), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
