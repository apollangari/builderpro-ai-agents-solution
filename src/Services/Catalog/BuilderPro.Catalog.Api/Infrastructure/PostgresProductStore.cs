using BuilderPro.Catalog.Api.Domain;
using Npgsql;

namespace BuilderPro.Catalog.Api.Infrastructure;

/// <summary>
/// Postgres store with plain SQL (no ORM), same style as the Identity service.
/// Every query has "tenant_id = $1": there is no method that reads across tenants.
/// Schema changes are additive and versioned in catalog.schema_history.
/// </summary>
public sealed class PostgresProductStore(NpgsqlDataSource db) : IProductStore
{
    private const string Columns = "tenant_id, sku, name, description, unit, list_price, currency, active";

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
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
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Product>> ListActiveAsync(string tenantId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM catalog.products WHERE tenant_id = $1 AND active ORDER BY sku");
        cmd.Parameters.AddWithValue(tenantId);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<Product?> FindActiveAsync(string tenantId, string sku, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM catalog.products WHERE tenant_id = $1 AND sku = $2 AND active");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(sku);
        return (await ReadAllAsync(cmd, ct)).SingleOrDefault();
    }

    public async Task UpsertAsync(Product p, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO catalog.products(tenant_id, sku, name, description, unit, list_price, currency, active)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (tenant_id, sku) DO UPDATE
                SET name = EXCLUDED.name, description = EXCLUDED.description, unit = EXCLUDED.unit,
                    list_price = EXCLUDED.list_price, currency = EXCLUDED.currency, active = EXCLUDED.active,
                    updated_at = now()
            """);
        cmd.Parameters.AddWithValue(p.TenantId);
        cmd.Parameters.AddWithValue(p.Sku);
        cmd.Parameters.AddWithValue(p.Name);
        cmd.Parameters.AddWithValue((object?)p.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(p.Unit);
        cmd.Parameters.AddWithValue(p.ListPrice);
        cmd.Parameters.AddWithValue(p.Currency);
        cmd.Parameters.AddWithValue(p.Active);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        return await cmd.ExecuteScalarAsync(ct) is 1;
    }

    private static async Task<IReadOnlyList<Product>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<Product>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Product(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetString(6),
                reader.GetBoolean(7)));
        }
        return rows;
    }
}
