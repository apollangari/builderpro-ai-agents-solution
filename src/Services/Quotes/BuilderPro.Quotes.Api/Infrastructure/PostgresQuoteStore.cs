using BuilderPro.Quotes.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api.Infrastructure;

/// <summary>
/// Postgres store: hand-written SQL, Dapper only for parameters and row mapping.
/// A quote and its lines are written in ONE transaction. Every read filters by tenant_id
/// (and customer_id for customers).
/// </summary>
public sealed class PostgresQuoteStore(NpgsqlDataSource db) : IQuoteStore
{
    private const string HeadColumns = """
        id AS Id, tenant_id AS TenantId, customer_id AS CustomerId, created_by AS CreatedBy, status AS Status,
        currency AS Currency, subtotal AS Subtotal, tax_rate AS TaxRate, tax AS Tax, total AS Total, created_at AS CreatedAt,
        zip AS Zip, guided_request::text AS GuidedRequest,
        number AS Number, valid_until AS ValidUntil, project_id AS ProjectId, accepted_at AS AcceptedAt
        """;

    // @customerId NULL = staff: every customer of the tenant.
    private const string Visible = "tenant_id = @tenantId AND (@customerId::text IS NULL OR customer_id = @customerId)";

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
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

            -- v2 (2n-e): guided quotes keep their ZIP, the request they were built from, and line groups.
            ALTER TABLE quotes.quotes ADD COLUMN IF NOT EXISTS zip text NULL;
            ALTER TABLE quotes.quotes ADD COLUMN IF NOT EXISTS guided_request jsonb NULL;
            ALTER TABLE quotes.quote_lines ADD COLUMN IF NOT EXISTS line_group text NULL;
            INSERT INTO quotes.schema_history(version) VALUES (2) ON CONFLICT DO NOTHING;

            -- v3 (2n-f): numbered quotes issued from a project, valid until a date, accepted by the customer.
            ALTER TABLE quotes.quotes ADD COLUMN IF NOT EXISTS number integer NULL;
            ALTER TABLE quotes.quotes ADD COLUMN IF NOT EXISTS valid_until timestamptz NULL;
            ALTER TABLE quotes.quotes ADD COLUMN IF NOT EXISTS project_id uuid NULL;
            ALTER TABLE quotes.quotes ADD COLUMN IF NOT EXISTS accepted_at timestamptz NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_quotes_number ON quotes.quotes(tenant_id, number) WHERE number IS NOT NULL;
            INSERT INTO quotes.schema_history(version) VALUES (3) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task CreateAsync(Quote q, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await InsertAsync(conn, tx, q, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<Quote> IssueAsync(Quote q, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // One numbering at a time per store (transaction-scoped advisory lock), then max + 1.
        await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext('quote-number:' || @t))", new { t = q.TenantId }, tx, cancellationToken: ct));
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(number), 1000) + 1 FROM quotes.quotes WHERE tenant_id = @t", new { t = q.TenantId }, tx, cancellationToken: ct));
        var numbered = q with { Issue = q.Issue! with { Number = next } };
        await InsertAsync(conn, tx, numbered, ct);
        await tx.CommitAsync(ct);
        return numbered;
    }

    public async Task<bool> AcceptAsync(string tenantId, string customerId, Guid id, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE quotes.quotes SET status = 'accepted', accepted_at = @at
            WHERE id = @id AND tenant_id = @tenantId AND customer_id = @customerId AND status = 'issued' AND valid_until > @at
            """, new { id, tenantId, customerId, at = at.UtcDateTime }, cancellationToken: ct)) == 1;
    }

    private static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Quote q, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.quotes(id, tenant_id, customer_id, created_by, status, currency, subtotal, tax_rate, tax, total, created_at, zip, guided_request,
                                      number, valid_until, project_id, accepted_at)
            VALUES (@Id, @TenantId, @CustomerId, @CreatedBy, @Status, @Currency, @Subtotal, @TaxRate, @Tax, @Total, @CreatedAt, @Zip, @GuidedRequest::jsonb,
                    @Number, @ValidUntil, @ProjectId, @AcceptedAt)
            """,
            new
            {
                q.Id, q.TenantId, q.CustomerId, q.CreatedBy, q.Status,
                q.Priced.Total.Currency,
                Subtotal = q.Priced.Subtotal.Amount, q.Priced.TaxRate, Tax = q.Priced.Tax.Amount, Total = q.Priced.Total.Amount,
                CreatedAt = q.CreatedAt.UtcDateTime,
                Zip = q.Guided?.Zip, GuidedRequest = q.Guided?.RequestJson,
                Number = q.Issue?.Number, ValidUntil = q.Issue?.ValidUntil.UtcDateTime, ProjectId = q.Issue?.ProjectId,
                AcceptedAt = q.Issue?.AcceptedAt?.UtcDateTime,
            },
            tx, cancellationToken: ct));

        // Dapper runs the statement once per element: one INSERT per line, same transaction.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.quote_lines(quote_id, line_no, sku, name, unit, quantity, unit_price, line_total, line_group)
            VALUES (@QuoteId, @LineNo, @Sku, @Name, @Unit, @Quantity, @UnitPrice, @LineTotal, @LineGroup)
            """,
            q.Priced.Lines.Select((l, i) => new
            {
                QuoteId = q.Id, LineNo = i, l.Sku, l.Name, l.Unit, l.Quantity,
                UnitPrice = l.UnitPrice.Amount, LineTotal = l.LineTotal.Amount,
                LineGroup = q.Guided?.LineGroups.ElementAtOrDefault(i),
            }),
            tx, cancellationToken: ct));
    }

    public async Task<Quote?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        var quotes = await ReadAsync($"SELECT {HeadColumns} FROM quotes.quotes WHERE {Visible} AND id = @id", new { tenantId, customerId, id }, ct);
        return quotes.Count == 0 ? null : quotes[0];
    }

    public Task<IReadOnlyList<Quote>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct) =>
        ReadAsync($"SELECT {HeadColumns} FROM quotes.quotes WHERE {Visible} ORDER BY created_at DESC LIMIT @limit", new { tenantId, customerId, limit }, ct);

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)) == 1;
    }

    private async Task<IReadOnlyList<Quote>> ReadAsync(string headSql, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var heads = (await conn.QueryAsync<HeadRow>(new CommandDefinition(headSql, args, cancellationToken: ct))).ToList();
        if (heads.Count == 0) return [];

        // All lines of all quotes in ONE query (no N+1). Npgsql passes the Guid[] as a Postgres array.
        var lines = (await conn.QueryAsync<LineRow>(new CommandDefinition("""
            SELECT quote_id AS QuoteId, sku AS Sku, name AS Name, unit AS Unit, quantity AS Quantity,
                   unit_price AS UnitPrice, line_total AS LineTotal, line_group AS LineGroup
            FROM quotes.quote_lines WHERE quote_id = ANY(@ids) ORDER BY quote_id, line_no
            """, new { ids = heads.Select(h => h.Id).ToArray() }, cancellationToken: ct)))
            .ToLookup(l => l.QuoteId);

        return [.. heads.Select(h =>
        {
            Money M(decimal amount) => new(amount, h.Currency);
            var rows = lines[h.Id].ToArray();
            var priced = rows.Select(l => new PricedLine(l.Sku, l.Name, l.Unit, l.Quantity, M(l.UnitPrice), M(l.LineTotal))).ToArray();
            var guided = h.Zip is null || h.GuidedRequest is null ? null : new GuidedInfo(h.Zip, h.GuidedRequest, [.. rows.Select(l => l.LineGroup ?? "")]);
            return new Quote(h.Id, h.TenantId, h.CustomerId, h.CreatedBy, h.Status,
                new PricedQuote(priced, M(h.Subtotal), h.TaxRate, M(h.Tax), M(h.Total)),
                new DateTimeOffset(DateTime.SpecifyKind(h.CreatedAt, DateTimeKind.Utc)), guided,
                h.Number is int n && h.ValidUntil is DateTime until && h.ProjectId is Guid pid
                    ? new QuoteIssue(n, Utc(until), pid, h.AcceptedAt is DateTime acc ? Utc(acc) : null)
                    : null);
        })];
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private sealed class HeadRow
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public string? CustomerId { get; init; }
        public string CreatedBy { get; init; } = "";
        public string Status { get; init; } = "";
        public string Currency { get; init; } = "";
        public decimal Subtotal { get; init; }
        public decimal TaxRate { get; init; }
        public decimal Tax { get; init; }
        public decimal Total { get; init; }
        public DateTime CreatedAt { get; init; }   // timestamptz arrives as UTC DateTime
        public string? Zip { get; init; }
        public string? GuidedRequest { get; init; }
        public int? Number { get; init; }
        public DateTime? ValidUntil { get; init; }
        public Guid? ProjectId { get; init; }
        public DateTime? AcceptedAt { get; init; }
    }

    private sealed class LineRow
    {
        public Guid QuoteId { get; init; }
        public string Sku { get; init; } = "";
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal LineTotal { get; init; }
        public string? LineGroup { get; init; }
    }
}
