using System.Text.Json;
using BuilderPro.Catalog.Api.Domain;
using Microsoft.Extensions.Caching.Distributed;

namespace BuilderPro.Catalog.Api.Infrastructure;

/// <summary>
/// Cache-aside for the hottest read in the catalog: "all active products of tenant T" (read on
/// every catalog page view and every quote). One Redis entry per tenant.
///
///  - Writes go to Postgres first, THEN the tenant's entry is deleted. The next read rebuilds it.
///  - Single-product reads are answered from the same cached list.
///  - Redis errors fall through to Postgres: the cache is an optimisation, never the source of truth.
/// </summary>
public sealed class CachingProductStore(IProductStore inner, IDistributedCache cache, TimeSpan ttl, ILogger<CachingProductStore> log) : IProductStore
{
    public static string KeyFor(string tenantId) => $"bp:catalog:products:{tenantId}";

    public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
    public Task<bool> PingAsync(CancellationToken ct) => inner.PingAsync(ct);

    public async Task<IReadOnlyList<Product>> ListActiveAsync(string tenantId, CancellationToken ct)
    {
        var key = KeyFor(tenantId);
        try
        {
            var bytes = await cache.GetAsync(key, ct);
            if (bytes is not null && JsonSerializer.Deserialize<Product[]>(bytes) is { } hit) return hit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Catalog cache read failed; reading Postgres");
        }

        var products = await inner.ListActiveAsync(tenantId, ct);
        try
        {
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(products),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Catalog cache write failed");
        }
        return products;
    }

    public async Task<Product?> FindActiveAsync(string tenantId, string sku, CancellationToken ct) =>
        (await ListActiveAsync(tenantId, ct)).FirstOrDefault(p => p.Sku == sku);

    public async Task UpsertAsync(Product product, CancellationToken ct)
    {
        await inner.UpsertAsync(product, ct);   // source of truth first
        try
        {
            await cache.RemoveAsync(KeyFor(product.TenantId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not invalidate catalog cache for {Tenant}; it expires with its TTL", product.TenantId);
        }
    }
}
