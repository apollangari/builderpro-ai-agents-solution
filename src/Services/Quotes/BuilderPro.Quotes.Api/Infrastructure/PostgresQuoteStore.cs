using BuilderPro.Quotes.Domain;
using Npgsql;

namespace BuilderPro.Quotes.Api.Infrastructure;

/// <summary>
/// Postgres store with plain SQL, same style as Identity and Catalog. A quote and its lines are
/// written in ONE transaction. Every read filters by tenant_id (and customer_id for customers).
/// </summary>
public sealed class PostgresQuoteStore(NpgsqlDataSource db) : IQuoteStore
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS quotes.quotes(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                customer_id text NULL,
                created_by text NOT NULL,
                status text NOT NULL,
                currency char(3) NOT NULL,
                subtotal numeric(14,2) NOT NULL,
                tax_rate numeric(6,5) NOT NULL,
                tax numeric(14,2) NOT NULL,
                total numeric(14,2) NOT NULL,
                created_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_quotes_tenant ON quotes.quotes(tenant_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS quotes.quote_lines(
                quote_id uuid NOT NULL REFERENCES quotes.quotes(id),
                line_no integer NOT NULL,
                sku text NOT NULL,
                name text NOT NULL,
                unit text NOT NULL,
                quantity numeric(12,3) NOT NULL,
                unit_price numeric(12,2) NOT NULL,
                line_total numeric(14,2) NOT NULL,
                PRIMARY KEY (quote_id, line_no));
            INSERT INTO quotes.schema_history(version) VALUES (1) ON CONFLICT DO NOTHING;
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateAsync(Quote q, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO quotes.quotes(id, tenant_id, customer_id, created_by, status, currency, subtotal, tax_rate, tax, total, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue(q.Id);
            cmd.Parameters.AddWithValue(q.TenantId);
            cmd.Parameters.AddWithValue((object?)q.CustomerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(q.CreatedBy);
            cmd.Parameters.AddWithValue(q.Status);
            cmd.Parameters.AddWithValue(q.Priced.Total.Currency);
            cmd.Parameters.AddWithValue(q.Priced.Subtotal.Amount);
            cmd.Parameters.AddWithValue(q.Priced.TaxRate);
            cmd.Parameters.AddWithValue(q.Priced.Tax.Amount);
            cmd.Parameters.AddWithValue(q.Priced.Total.Amount);
            cmd.Parameters.AddWithValue(q.CreatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var batch = new NpgsqlBatch(conn, tx);
        for (var i = 0; i < q.Priced.Lines.Count; i++)
        {
            var l = q.Priced.Lines[i];
            var c = new NpgsqlBatchCommand("""
                INSERT INTO quotes.quote_lines(quote_id, line_no, sku, name, unit, quantity, unit_price, line_total)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """);
            c.Parameters.AddWithValue(q.Id);
            c.Parameters.AddWithValue(i);
            c.Parameters.AddWithValue(l.Sku);
            c.Parameters.AddWithValue(l.Name);
            c.Parameters.AddWithValue(l.Unit);
            c.Parameters.AddWithValue(l.Quantity);
            c.Parameters.AddWithValue(l.UnitPrice.Amount);
            c.Parameters.AddWithValue(l.LineTotal.Amount);
            batch.BatchCommands.Add(c);
        }
        await batch.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<Quote?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        var heads = await ReadHeadsAsync("AND id = $3", ct, tenantId, customerId, id);
        return heads.Count == 0 ? null : (await WithLinesAsync(heads, ct))[0];
    }

    public async Task<IReadOnlyList<Quote>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct)
    {
        var heads = await ReadHeadsAsync("ORDER BY created_at DESC LIMIT $3", ct, tenantId, customerId, limit);
        return await WithLinesAsync(heads, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        return await cmd.ExecuteScalarAsync(ct) is 1;
    }

    private sealed record Head(Guid Id, string TenantId, string? CustomerId, string CreatedBy, string Status, string Currency,
        decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total, DateTimeOffset CreatedAt);

    // $1 = tenant, $2 = customer (NULL = staff: every customer of the tenant), $3 = caller-specific.
    private async Task<List<Head>> ReadHeadsAsync(string tail, CancellationToken ct, string tenantId, string? customerId, object third)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT id, tenant_id, customer_id, created_by, status, currency, subtotal, tax_rate, tax, total, created_at
            FROM quotes.quotes
            WHERE tenant_id = $1 AND ($2::text IS NULL OR customer_id = $2)
            {tail}
            """);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)customerId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(third);

        var heads = new List<Head>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            heads.Add(new Head(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetFieldValue<DateTimeOffset>(10)));
        }
        return heads;
    }

    private async Task<IReadOnlyList<Quote>> WithLinesAsync(List<Head> heads, CancellationToken ct)
    {
        if (heads.Count == 0) return [];

        await using var cmd = db.CreateCommand("""
            SELECT quote_id, sku, name, unit, quantity, unit_price, line_total
            FROM quotes.quote_lines WHERE quote_id = ANY($1) ORDER BY quote_id, line_no
            """);
        cmd.Parameters.AddWithValue(heads.Select(h => h.Id).ToArray());

        var lines = heads.ToDictionary(h => h.Id, _ => new List<PricedLine>());
        var currency = heads.ToDictionary(h => h.Id, h => h.Currency);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                var cur = currency[id];
                lines[id].Add(new PricedLine(r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4),
                    new Money(r.GetDecimal(5), cur), new Money(r.GetDecimal(6), cur)));
            }
        }

        return [.. heads.Select(h => new Quote(h.Id, h.TenantId, h.CustomerId, h.CreatedBy, h.Status,
            new PricedQuote(lines[h.Id], new Money(h.Subtotal, h.Currency), h.TaxRate, new Money(h.Tax, h.Currency), new Money(h.Total, h.Currency)),
            h.CreatedAt))];
    }
}
