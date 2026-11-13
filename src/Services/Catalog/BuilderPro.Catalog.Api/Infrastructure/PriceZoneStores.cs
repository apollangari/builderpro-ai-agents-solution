using BuilderPro.Catalog.Api.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Catalog.Api.Infrastructure;

/// <summary>
/// Price zones in Postgres (schema v3, additive). Every query filters by tenant_id. Not cached:
/// both reads are single indexed lookups; a cache can come later with the same invalidation rules as 2f.
/// </summary>
public sealed class PostgresPriceZoneStore(NpgsqlDataSource db) : IPriceZoneStore
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS catalog;
            CREATE TABLE IF NOT EXISTS catalog.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v3 (2n-a): price zones. A ZIP is in at most one zone per tenant.
            CREATE TABLE IF NOT EXISTS catalog.price_zones(
                tenant_id text NOT NULL,
                zone_id text NOT NULL,
                name text NOT NULL,
                PRIMARY KEY (tenant_id, zone_id));
            CREATE TABLE IF NOT EXISTS catalog.zone_zips(
                tenant_id text NOT NULL,
                zip text NOT NULL,
                zone_id text NOT NULL,
                PRIMARY KEY (tenant_id, zip),
                FOREIGN KEY (tenant_id, zone_id) REFERENCES catalog.price_zones(tenant_id, zone_id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS catalog.zone_prices(
                tenant_id text NOT NULL,
                sku text NOT NULL,
                zone_id text NOT NULL,
                price numeric(12,2) NOT NULL CHECK (price >= 0),
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (tenant_id, zone_id, sku),
                FOREIGN KEY (tenant_id, zone_id) REFERENCES catalog.price_zones(tenant_id, zone_id) ON DELETE CASCADE);
            INSERT INTO catalog.schema_history(version) VALUES (3) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task<string?> ZoneForZipAsync(string tenantId, string zip, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT zone_id FROM catalog.zone_zips WHERE tenant_id = @tenantId AND zip = @zip",
            new { tenantId, zip }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, decimal>> PricesAsync(string tenantId, string zoneId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Sku, decimal Price)>(new CommandDefinition(
            "SELECT sku, price FROM catalog.zone_prices WHERE tenant_id = @tenantId AND zone_id = @zoneId",
            new { tenantId, zoneId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Sku, r => r.Price, StringComparer.Ordinal);
    }

    public async Task UpsertZoneAsync(PriceZone zone, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO catalog.price_zones(tenant_id, zone_id, name) VALUES (@TenantId, @ZoneId, @Name)
            ON CONFLICT (tenant_id, zone_id) DO UPDATE SET name = EXCLUDED.name;
            DELETE FROM catalog.zone_zips WHERE tenant_id = @TenantId AND zone_id = @ZoneId;
            """, new { zone.TenantId, zone.ZoneId, zone.Name }, tx, cancellationToken: ct));
        foreach (var zip in zone.Zips)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO catalog.zone_zips(tenant_id, zip, zone_id) VALUES (@TenantId, @zip, @ZoneId)
                ON CONFLICT (tenant_id, zip) DO UPDATE SET zone_id = EXCLUDED.zone_id
                """, new { zone.TenantId, zip, zone.ZoneId }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task UpsertPriceAsync(ZonePrice p, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO catalog.zone_prices(tenant_id, sku, zone_id, price) VALUES (@TenantId, @Sku, @ZoneId, @Price)
            ON CONFLICT (tenant_id, zone_id, sku) DO UPDATE SET price = EXCLUDED.price, updated_at = now()
            """, new { p.TenantId, p.Sku, p.ZoneId, p.Price }, cancellationToken: ct));
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryPriceZoneStore : IPriceZoneStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string Zip), string> _zips = [];
    private readonly Dictionary<(string Tenant, string Zone, string Sku), decimal> _prices = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> ZoneForZipAsync(string tenantId, string zip, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_zips.TryGetValue((tenantId, zip), out var z) ? z : null);
    }

    public Task<IReadOnlyDictionary<string, decimal>> PricesAsync(string tenantId, string zoneId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, decimal>>(_prices
                .Where(kv => kv.Key.Tenant == tenantId && kv.Key.Zone == zoneId)
                .ToDictionary(kv => kv.Key.Sku, kv => kv.Value, StringComparer.Ordinal));
        }
    }

    public Task UpsertZoneAsync(PriceZone zone, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var key in _zips.Where(kv => kv.Key.Tenant == zone.TenantId && kv.Value == zone.ZoneId).Select(kv => kv.Key).ToList())
            {
                _zips.Remove(key);
            }
            foreach (var zip in zone.Zips) _zips[(zone.TenantId, zip)] = zone.ZoneId;
        }
        return Task.CompletedTask;
    }

    public Task UpsertPriceAsync(ZonePrice price, CancellationToken ct)
    {
        lock (_gate) _prices[(price.TenantId, price.ZoneId, price.Sku)] = price.Price;
        return Task.CompletedTask;
    }
}
