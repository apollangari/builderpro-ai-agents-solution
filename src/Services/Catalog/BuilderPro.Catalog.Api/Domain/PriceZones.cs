using System.Text.RegularExpressions;

namespace BuilderPro.Catalog.Api.Domain;

/// <summary>
/// A store's price zone (2n-a): a named set of ZIP / postal codes that share prices. A ZIP belongs
/// to at most one zone of a store. The ZIP lists never leave the server: the browser only learns
/// whether a ZIP is served and what each product costs there.
/// </summary>
public sealed record PriceZone(string TenantId, string ZoneId, string Name, IReadOnlyList<string> Zips);

/// <summary>A product's price in one zone. A product is sold in a zone only if it has a price there.</summary>
public sealed record ZonePrice(string TenantId, string Sku, string ZoneId, decimal Price);

public interface IPriceZoneStore
{
    Task InitializeAsync(CancellationToken ct);

    /// <summary>The zone of ONE tenant that contains this (normalized) ZIP, or null when the store doesn't serve it.</summary>
    Task<string?> ZoneForZipAsync(string tenantId, string zip, CancellationToken ct);

    /// <summary>SKU -> price for one zone of ONE tenant.</summary>
    Task<IReadOnlyDictionary<string, decimal>> PricesAsync(string tenantId, string zoneId, CancellationToken ct);

    /// <summary>Insert or replace a zone and its ZIP list (a ZIP moves if another zone had it). Dev seed; admin API later.</summary>
    Task UpsertZoneAsync(PriceZone zone, CancellationToken ct);

    Task UpsertPriceAsync(ZonePrice price, CancellationToken ct);
}

/// <summary>ZIP / postal code rules: US ZIP (ZIP+4 is cut to 5 digits) and Canadian postal codes.</summary>
public static partial class Zip
{
    /// <summary>"75201", "75201-1234" -> "75201"; "k1a 0b1" -> "K1A 0B1"; anything else -> null.</summary>
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var us = UsZip().Match(s);
        if (us.Success) return us.Groups[1].Value;
        var ca = CaPostal().Match(s.ToUpperInvariant());
        return ca.Success ? $"{ca.Groups[1].Value} {ca.Groups[2].Value}" : null;
    }

    [GeneratedRegex(@"^(\d{5})(?:-\d{4})?$")]
    private static partial Regex UsZip();

    [GeneratedRegex(@"^([A-Z]\d[A-Z]) ?(\d[A-Z]\d)$")]
    private static partial Regex CaPostal();
}
