using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Catalog.Api.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BuilderPro.Catalog.Api.Tests;

public sealed class CachingProductStoreTests
{
    /// <summary>The real store's stand-in, counting reads.</summary>
    private sealed class CountingStore : IProductStore
    {
        private readonly InMemoryProductStore _inner = new();
        public int Reads { get; private set; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
        public Task UpsertAsync(Product product, CancellationToken ct) => _inner.UpsertAsync(product, ct);

        public Task<IReadOnlyList<Product>> ListActiveAsync(string tenantId, CancellationToken ct)
        {
            Reads++;
            return _inner.ListActiveAsync(tenantId, ct);
        }

        public Task<Product?> FindActiveAsync(string tenantId, string sku, CancellationToken ct)
        {
            Reads++;
            return _inner.FindActiveAsync(tenantId, sku, ct);
        }
    }

    private static (CachingProductStore Store, CountingStore Inner) Create()
    {
        var inner = new CountingStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return (new CachingProductStore(inner, cache, TimeSpan.FromMinutes(5), NullLogger<CachingProductStore>.Instance), inner);
    }

    private static Product P(string tenant, string sku, decimal price, bool active = true) => new(tenant, sku, sku, null, "each", price, "USD", active);

    [Fact]
    public async Task Repeat_reads_hit_the_cache_and_single_lookups_reuse_the_list()
    {
        var (store, inner) = Create();
        await store.UpsertAsync(P("t-acme", "A", 1m), default);

        await store.ListActiveAsync("t-acme", default);
        await store.ListActiveAsync("t-acme", default);
        var a = await store.FindActiveAsync("t-acme", "A", default);

        Assert.Equal(1, inner.Reads);
        Assert.Equal(1m, a!.ListPrice);
    }

    [Fact]
    public async Task A_write_invalidates_only_that_tenant()
    {
        var (store, inner) = Create();
        await store.UpsertAsync(P("t-acme", "A", 1m), default);
        await store.UpsertAsync(P("t-globex", "G", 2m), default);
        await store.ListActiveAsync("t-acme", default);
        await store.ListActiveAsync("t-globex", default);

        await store.UpsertAsync(P("t-acme", "A", 9.99m), default);   // price change

        Assert.Equal(9.99m, (await store.FindActiveAsync("t-acme", "A", default))!.ListPrice);   // fresh, not stale
        await store.ListActiveAsync("t-globex", default);
        Assert.Equal(3, inner.Reads);   // acme re-read once; globex still cached
    }

    [Fact]
    public async Task Tenants_never_share_a_cache_entry()
    {
        var (store, _) = Create();
        await store.UpsertAsync(P("t-acme", "PAINT", 34.98m), default);
        await store.UpsertAsync(P("t-globex", "PAINT", 42.00m), default);

        Assert.Equal(34.98m, (await store.FindActiveAsync("t-acme", "PAINT", default))!.ListPrice);
        Assert.Equal(42.00m, (await store.FindActiveAsync("t-globex", "PAINT", default))!.ListPrice);
    }

    [Fact]
    public async Task Deactivating_a_product_removes_it_from_the_cached_list()
    {
        var (store, _) = Create();
        await store.UpsertAsync(P("t-acme", "A", 1m), default);
        await store.ListActiveAsync("t-acme", default);

        await store.UpsertAsync(P("t-acme", "A", 1m, active: false), default);

        Assert.Empty(await store.ListActiveAsync("t-acme", default));
    }
}
