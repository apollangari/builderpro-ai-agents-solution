namespace BuilderPro.Catalog.Api.Domain;

/// <summary>
/// A product a retailer (tenant) sells. The key is (TenantId, Sku): two retailers may use the
/// same SKU for different things, and neither can ever see the other's row.
///
/// ListPrice is DATA the retailer maintains, not a computed price. Quotes (2c/2d) compute
/// totals from it in domain code; an agent never does arithmetic on it.
/// </summary>
public sealed record Product(
    string TenantId,
    string Sku,
    string Name,
    string? Description,
    string Unit,            // "each", "m2", "m", "litre" ... how ListPrice is measured
    decimal ListPrice,      // money is decimal, never double
    string Currency,        // ISO 4217, e.g. "USD"
    bool Active);           // inactive products are hidden, not deleted (quotes may reference them)

public interface IProductStore
{
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Active products of ONE tenant, ordered by SKU. The tenant is a required argument on purpose.</summary>
    Task<IReadOnlyList<Product>> ListActiveAsync(string tenantId, CancellationToken ct);

    /// <summary>One active product of ONE tenant, or null. Another tenant's SKU is indistinguishable from a missing one.</summary>
    Task<Product?> FindActiveAsync(string tenantId, string sku, CancellationToken ct);

    /// <summary>Insert or update by (TenantId, Sku). Idempotent. Used by the dev seed; the admin API comes later.</summary>
    Task UpsertAsync(Product product, CancellationToken ct);

    Task<bool> PingAsync(CancellationToken ct);
}
