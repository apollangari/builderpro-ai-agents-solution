using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// A 5 % advance on an accepted quote (6a). Paid = the quote's price is held and the project moves on
/// to the measure visit. Amount is computed by DepositPolicy from the STORED quote total, never sent in.
/// </summary>
public sealed record Deposit(
    Guid Id, string TenantId, Guid ProjectId, Guid QuoteId, string CustomerId,
    decimal Amount, string Currency, decimal Percent, string Status, string Provider,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, string? PaidBy = null)
{
    public bool Paid => Status == DepositStatus.Paid;
}

public static class DepositStatus
{
    public const string Pending = "pending";
    public const string Paid = "paid";
}

public interface IDepositStore
{
    Task InitializeAsync(CancellationToken ct);
    Task CreateAsync(Deposit deposit, CancellationToken ct);
    Task<Deposit?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct);

    /// <summary>The latest deposit for this quote (pending or paid), or null.</summary>
    Task<Deposit?> ForQuoteAsync(string tenantId, Guid quoteId, CancellationToken ct);

    /// <summary>pending -> paid, once. False when it isn't pending (already paid, or not this tenant's).</summary>
    Task<bool> MarkPaidAsync(string tenantId, Guid id, DateTimeOffset at, string paidBy, CancellationToken ct);
}

/// <summary>Deposits in Postgres (quotes schema v5).</summary>
public sealed class PostgresDepositStore(NpgsqlDataSource db) : IDepositStore
{
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, project_id AS ProjectId, quote_id AS QuoteId, customer_id AS CustomerId,
        amount AS Amount, currency AS Currency, percent AS Percent, status AS Status, provider AS Provider,
        created_at AS CreatedAt, paid_at AS PaidAt, paid_by AS PaidBy
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v5 (6a): deposits. One paid deposit per quote at most.
            CREATE TABLE IF NOT EXISTS quotes.deposits(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                project_id uuid NOT NULL,
                quote_id uuid NOT NULL,
                customer_id text NOT NULL,
                amount numeric(14,2) NOT NULL CHECK (amount > 0),
                currency char(3) NOT NULL,
                percent numeric(5,2) NOT NULL,
                status text NOT NULL CHECK (status IN ('pending','paid')),
                provider text NOT NULL,
                created_at timestamptz NOT NULL,
                paid_at timestamptz NULL,
                paid_by text NULL);
            CREATE INDEX IF NOT EXISTS ix_deposits_quote ON quotes.deposits(tenant_id, quote_id, created_at DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_deposits_paid ON quotes.deposits(quote_id) WHERE status = 'paid';
            INSERT INTO quotes.schema_history(version) VALUES (5) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task CreateAsync(Deposit d, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.deposits(id, tenant_id, project_id, quote_id, customer_id, amount, currency, percent, status, provider, created_at)
            VALUES (@Id, @TenantId, @ProjectId, @QuoteId, @CustomerId, @Amount, @Currency, @Percent, @Status, @Provider, @CreatedAt)
            """, new { d.Id, d.TenantId, d.ProjectId, d.QuoteId, d.CustomerId, d.Amount, d.Currency, d.Percent, d.Status, d.Provider, CreatedAt = d.CreatedAt.UtcDateTime },
            cancellationToken: ct));
    }

    public async Task<Deposit?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.deposits WHERE id = @id AND tenant_id = @tenantId AND (@customerId::text IS NULL OR customer_id = @customerId)",
            new { id, tenantId, customerId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<Deposit?> ForQuoteAsync(string tenantId, Guid quoteId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.deposits WHERE tenant_id = @tenantId AND quote_id = @quoteId ORDER BY (status = 'paid') DESC, created_at DESC LIMIT 1",
            new { tenantId, quoteId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<bool> MarkPaidAsync(string tenantId, Guid id, DateTimeOffset at, string paidBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE quotes.deposits SET status = 'paid', paid_at = @at, paid_by = @paidBy WHERE id = @id AND tenant_id = @tenantId AND status = 'pending'",
            new { id, tenantId, at = at.UtcDateTime, paidBy }, cancellationToken: ct)) == 1;
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public Guid ProjectId { get; init; }
        public Guid QuoteId { get; init; }
        public string CustomerId { get; init; } = "";
        public decimal Amount { get; init; }
        public string Currency { get; init; } = "";
        public decimal Percent { get; init; }
        public string Status { get; init; } = "";
        public string Provider { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime? PaidAt { get; init; }
        public string? PaidBy { get; init; }

        public Deposit ToDomain() => new(Id, TenantId, ProjectId, QuoteId, CustomerId, Amount, Currency.Trim(), Percent, Status, Provider,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            PaidAt is DateTime p ? new DateTimeOffset(DateTime.SpecifyKind(p, DateTimeKind.Utc)) : null, PaidBy);
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryDepositStore : IDepositStore
{
    private readonly Lock _gate = new();
    private readonly List<Deposit> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CreateAsync(Deposit deposit, CancellationToken ct)
    {
        lock (_gate) _rows.Add(deposit);
        return Task.CompletedTask;
    }

    public Task<Deposit?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rows.FirstOrDefault(d => d.Id == id && d.TenantId == tenantId && (customerId is null || d.CustomerId == customerId)));
    }

    public Task<Deposit?> ForQuoteAsync(string tenantId, Guid quoteId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Where(d => d.TenantId == tenantId && d.QuoteId == quoteId)
                .OrderByDescending(d => d.Paid).ThenByDescending(d => d.CreatedAt).FirstOrDefault());
        }
    }

    public Task<bool> MarkPaidAsync(string tenantId, Guid id, DateTimeOffset at, string paidBy, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _rows.FindIndex(d => d.Id == id && d.TenantId == tenantId && d.Status == DepositStatus.Pending);
            if (i < 0) return Task.FromResult(false);
            _rows[i] = _rows[i] with { Status = DepositStatus.Paid, PaidAt = at, PaidBy = paidBy };
            return Task.FromResult(true);
        }
    }
}
