using BuilderPro.Catalog.Api.Domain;

namespace BuilderPro.Catalog.Api.Infrastructure;

/// <summary>For tests only. Production always uses Postgres; there is no silent fallback.</summary>
public sealed class InMemoryProductStore : IProductStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string Sku), Product> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<Product>> ListActiveAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Product>>(
                [.. _rows.Values.Where(p => p.TenantId == tenantId && p.Active).OrderBy(p => p.Sku, StringComparer.Ordinal)]);
        }
    }

    public Task<Product?> FindActiveAsync(string tenantId, string sku, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue((tenantId, sku), out var p) && p.Active ? p : null);
        }
    }

    public Task UpsertAsync(Product product, CancellationToken ct)
    {
        lock (_gate)
        {
            _rows[(product.TenantId, product.Sku)] = product;
            return Task.CompletedTask;
        }
    }
}
