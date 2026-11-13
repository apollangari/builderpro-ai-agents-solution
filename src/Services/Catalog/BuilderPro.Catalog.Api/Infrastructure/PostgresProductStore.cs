using System.Text.Json;
using BuilderPro.Catalog.Api.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Catalog.Api.Infrastructure;

/// <summary>
/// Postgres store: hand-written SQL, Dapper only for parameters and row mapping.
/// Every query has "tenant_id = @tenantId": there is no method that reads across tenants.
/// Schema changes are additive and versioned in catalog.schema_history.
/// </summary>
public sealed class PostgresProductStore(NpgsqlDataSource db) : IProductStore
{
    // Aliases match ProductRow; attributes travel as JSON text (jsonb in the table).
    private const string Columns = """
        tenant_id AS TenantId, sku AS Sku, name AS Name, description AS Description, unit AS Unit,
        list_price AS ListPrice, currency AS Currency, active AS Active,
        category AS Category, image_url AS ImageUrl, attributes::text AS AttributesJson
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS catalog;
            CREATE TABLE IF NOT EXISTS catalog.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS catalog.products(
                tenant_id text NOT NULL,
                sku text NOT NULL,
                name text NOT NULL,
                description text NULL,
                unit text NOT NULL,
                list_price numeric(12,2) NOT NULL CHECK (list_price >= 0),
                currency char(3) NOT NULL,
                active boolean NOT NULL DEFAULT true,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (tenant_id, sku));
            INSERT INTO catalog.schema_history(version) VALUES (1) ON CONFLICT DO NOTHING;

            -- v2 (2h): categories, attributes, sample images. Additive only.
            ALTER TABLE catalog.products ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT 'other';
            ALTER TABLE catalog.products ADD COLUMN IF NOT EXISTS attributes jsonb NOT NULL DEFAULT '{}'::jsonb;
            ALTER TABLE catalog.products ADD COLUMN IF NOT EXISTS image_url text NULL;
            CREATE INDEX IF NOT EXISTS ix_products_category ON catalog.products(tenant_id, category) WHERE active;
            INSERT INTO catalog.schema_history(version) VALUES (2) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Product>> ListActiveAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return [.. (await conn.QueryAsync<ProductRow>(new CommandDefinition(
            $"SELECT {Columns} FROM catalog.products WHERE tenant_id = @tenantId AND active ORDER BY sku",
            new { tenantId }, cancellationToken: ct))).Select(r => r.ToDomain())];
    }

    public async Task<Product?> FindActiveAsync(string tenantId, string sku, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QuerySingleOrDefaultAsync<ProductRow>(new CommandDefinition(
            $"SELECT {Columns} FROM catalog.products WHERE tenant_id = @tenantId AND sku = @sku AND active",
            new { tenantId, sku }, cancellationToken: ct)))?.ToDomain();
    }

    public async Task UpsertAsync(Product p, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO catalog.products(tenant_id, sku, name, description, unit, list_price, currency, active, category, image_url, attributes)
            VALUES (@TenantId, @Sku, @Name, @Description, @Unit, @ListPrice, @Currency, @Active, @Category, @ImageUrl, @AttributesJson::jsonb)
            ON CONFLICT (tenant_id, sku) DO UPDATE
                SET name = EXCLUDED.name, description = EXCLUDED.description, unit = EXCLUDED.unit,
                    list_price = EXCLUDED.list_price, currency = EXCLUDED.currency, active = EXCLUDED.active,
                    category = EXCLUDED.category, image_url = EXCLUDED.image_url, attributes = EXCLUDED.attributes,
                    updated_at = now()
            """,
            new
            {
                p.TenantId, p.Sku, p.Name, p.Description, p.Unit, p.ListPrice, p.Currency, p.Active,
                p.Category, p.ImageUrl, AttributesJson = JsonSerializer.Serialize(p.AttributeValues),
            },
            cancellationToken: ct));
    }

    private sealed class ProductRow
    {
        public string TenantId { get; init; } = "";
        public string Sku { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Unit { get; init; } = "";
        public decimal ListPrice { get; init; }
        public string Currency { get; init; } = "";
        public bool Active { get; init; }
        public string Category { get; init; } = "other";
        public string? ImageUrl { get; init; }
        public string AttributesJson { get; init; } = "{}";

        public Product ToDomain() => new(TenantId, Sku, Name, Description, Unit, ListPrice, Currency, Active, Category, ImageUrl,
            JsonSerializer.Deserialize<Dictionary<string, string>>(AttributesJson) ?? []);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)) == 1;
    }
}
