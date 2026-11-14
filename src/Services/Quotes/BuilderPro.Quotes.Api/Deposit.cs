using BuilderPro.Quotes.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// A 5 % advance on an accepted quote (6a). Succeeded = the quote's price is held and the project moves on
/// to the measure visit. Amount is computed by DepositPolicy from the STORED quote total, never sent in.
/// Its Status follows the payment state machine (6d, <see cref="PaymentStates"/>).
/// </summary>
public sealed record Deposit(
    Guid Id, string TenantId, Guid ProjectId, Guid QuoteId, string CustomerId,
    decimal Amount, string Currency, decimal Percent, string Status, string Provider,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, string? PaidBy = null,
    string? ProviderRef = null, string? CheckoutUrl = null, DateTimeOffset? CheckoutExpiresAt = null,   // 6d: the provider's checkout
    string Kind = PaymentKinds.Deposit)                                                                   // final gaps: deposit or balance
{
    public bool Paid => Status == PaymentStates.Succeeded;
}

/// <summary>What a payment is for: the 5 % deposit that holds the price, or the balance when the job is done.</summary>
public static class PaymentKinds
{
    public const string Deposit = "deposit";
    public const string Balance = "balance";
}

/// <summary>One recorded move of the payment state machine: the audit trail.</summary>
public sealed record PaymentEvent(long Id, Guid DepositId, string From, string To, DateTimeOffset At, string Source, string? Detail);

public interface IDepositStore
{
    Task InitializeAsync(CancellationToken ct);

    /// <summary>A new deposit in state "created" (its creation is recorded as the first event).</summary>
    Task CreateAsync(Deposit deposit, CancellationToken ct);

    Task<Deposit?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct);

    /// <summary>The latest deposit for this quote (a succeeded one first), or null.</summary>
    Task<Deposit?> ForQuoteAsync(string tenantId, Guid quoteId, CancellationToken ct);

    /// <summary>
    /// Moves the payment from -> to (checked by <see cref="PaymentStates.CanMove"/>) only if it is still in
    /// "from" (so two webhooks can't both win), and records the move. paidBy is set when it succeeds.
    /// False when it wasn't in "from" any more.
    /// </summary>
    Task<bool> TransitionAsync(string tenantId, Guid id, string from, string to, DateTimeOffset at, string source, string? detail, string? paidBy, CancellationToken ct);

    /// <summary>6d: remember where the customer pays (the provider's session and its URL).</summary>
    Task SetCheckoutAsync(string tenantId, Guid id, string providerRef, string url, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>The audit trail of one deposit, oldest first.</summary>
    Task<IReadOnlyList<PaymentEvent>> EventsAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>Payments waiting for an answer: unknown, or processing past the checkout's expiry (any tenant; the reconciler's work list).</summary>
    Task<IReadOnlyList<Deposit>> AwaitingAnswerAsync(DateTimeOffset now, int limit, CancellationToken ct);

    /// <summary>The project's SUCCEEDED deposit, or null (6b: a measured quote inherits it).</summary>
    Task<Deposit?> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct);

    /// <summary>Succeeded deposits of one tenant, paid in [from, to) (1i commission report).</summary>
    Task<IReadOnlyList<Deposit>> ListPaidAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Final gaps: the project's latest BALANCE payment (a succeeded one first), or null.</summary>
    Task<Deposit?> BalanceForProjectAsync(string tenantId, Guid projectId, CancellationToken ct);
}

/// <summary>Deposits in Postgres (quotes schema v5, v9, v10).</summary>
public sealed class PostgresDepositStore(NpgsqlDataSource db) : IDepositStore
{
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, project_id AS ProjectId, quote_id AS QuoteId, customer_id AS CustomerId,
        amount AS Amount, currency AS Currency, percent AS Percent, status AS Status, provider AS Provider,
        created_at AS CreatedAt, paid_at AS PaidAt, paid_by AS PaidBy,
        provider_ref AS ProviderRef, checkout_url AS CheckoutUrl, checkout_expires_at AS CheckoutExpiresAt, kind AS Kind
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v5 (6a): deposits.
            CREATE TABLE IF NOT EXISTS quotes.deposits(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                project_id uuid NOT NULL,
                quote_id uuid NOT NULL,
                customer_id text NOT NULL,
                amount numeric(14,2) NOT NULL CHECK (amount > 0),
                currency char(3) NOT NULL,
                percent numeric(5,2) NOT NULL,
                status text NOT NULL,
                provider text NOT NULL,
                created_at timestamptz NOT NULL,
                paid_at timestamptz NULL,
                paid_by text NULL);
            CREATE INDEX IF NOT EXISTS ix_deposits_quote ON quotes.deposits(tenant_id, quote_id, created_at DESC);
            INSERT INTO quotes.schema_history(version) VALUES (5) ON CONFLICT DO NOTHING;

            -- v9 (6d): the payment provider's checkout (Stripe session id and URL).
            ALTER TABLE quotes.deposits ADD COLUMN IF NOT EXISTS provider_ref text NULL;
            ALTER TABLE quotes.deposits ADD COLUMN IF NOT EXISTS checkout_url text NULL;
            ALTER TABLE quotes.deposits ADD COLUMN IF NOT EXISTS checkout_expires_at timestamptz NULL;
            INSERT INTO quotes.schema_history(version) VALUES (9) ON CONFLICT DO NOTHING;

            -- v10 (6d): the payment state machine. pending/paid become processing/succeeded; every move is recorded.
            ALTER TABLE quotes.deposits DROP CONSTRAINT IF EXISTS deposits_status_check;
            ALTER TABLE quotes.deposits DROP CONSTRAINT IF EXISTS deposits_payment_state;
            UPDATE quotes.deposits SET status = 'processing' WHERE status = 'pending';
            UPDATE quotes.deposits SET status = 'succeeded' WHERE status = 'paid';
            ALTER TABLE quotes.deposits ADD CONSTRAINT deposits_payment_state
                CHECK (status IN ('created','processing','succeeded','failed','unknown'));
            DROP INDEX IF EXISTS quotes.ux_deposits_paid;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_deposits_succeeded ON quotes.deposits(quote_id) WHERE status = 'succeeded';
            CREATE INDEX IF NOT EXISTS ix_deposits_awaiting ON quotes.deposits(checkout_expires_at) WHERE status IN ('processing','unknown');
            CREATE TABLE IF NOT EXISTS quotes.payment_events(
                id bigserial PRIMARY KEY,
                tenant_id text NOT NULL,
                deposit_id uuid NOT NULL,
                from_state text NOT NULL,
                to_state text NOT NULL,
                at timestamptz NOT NULL,
                source text NOT NULL,
                detail text NULL);
            CREATE INDEX IF NOT EXISTS ix_payment_events_deposit ON quotes.payment_events(tenant_id, deposit_id, id);
            INSERT INTO quotes.schema_history(version) VALUES (10) ON CONFLICT DO NOTHING;

            -- v13 (final gaps): a payment is a deposit or the balance.
            ALTER TABLE quotes.deposits ADD COLUMN IF NOT EXISTS kind text NOT NULL DEFAULT 'deposit';
            DROP INDEX IF EXISTS quotes.ux_deposits_succeeded;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_payments_succeeded ON quotes.deposits(quote_id, kind) WHERE status = 'succeeded';
            INSERT INTO quotes.schema_history(version) VALUES (13) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task CreateAsync(Deposit d, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.deposits(id, tenant_id, project_id, quote_id, customer_id, amount, currency, percent, status, provider, created_at, kind)
            VALUES (@Id, @TenantId, @ProjectId, @QuoteId, @CustomerId, @Amount, @Currency, @Percent, @Status, @Provider, @CreatedAt, @Kind)
            """, new { d.Id, d.TenantId, d.ProjectId, d.QuoteId, d.CustomerId, d.Amount, d.Currency, d.Percent, d.Status, d.Provider, CreatedAt = d.CreatedAt.UtcDateTime, d.Kind },
            tx, cancellationToken: ct));
        await AppendAsync(conn, tx, d.TenantId, d.Id, "", d.Status, d.CreatedAt, PaymentSources.Checkout, $"{d.Amount} {d.Currency} via {d.Provider}", ct);
        await tx.CommitAsync(ct);
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
            $"SELECT {Columns} FROM quotes.deposits WHERE tenant_id = @tenantId AND quote_id = @quoteId AND kind = 'deposit' ORDER BY (status = 'succeeded') DESC, created_at DESC LIMIT 1",
            new { tenantId, quoteId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<bool> TransitionAsync(string tenantId, Guid id, string from, string to, DateTimeOffset at, string source, string? detail, string? paidBy, CancellationToken ct)
    {
        if (!PaymentStates.CanMove(from, to)) throw new InvalidOperationException($"A payment can't move from {from} to {to}.");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var moved = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE quotes.deposits SET status = @to,
                   paid_at = CASE WHEN @to = 'succeeded' THEN @at ELSE paid_at END,
                   paid_by = CASE WHEN @to = 'succeeded' THEN @paidBy ELSE paid_by END
            WHERE id = @id AND tenant_id = @tenantId AND status = @from
            """, new { id, tenantId, from, to, at = at.UtcDateTime, paidBy }, tx, cancellationToken: ct)) == 1;
        if (!moved) return false;
        await AppendAsync(conn, tx, tenantId, id, from, to, at, source, detail, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task SetCheckoutAsync(string tenantId, Guid id, string providerRef, string url, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE quotes.deposits SET provider_ref = @providerRef, checkout_url = @url, checkout_expires_at = @expiresAt WHERE id = @id AND tenant_id = @tenantId AND status IN ('created','processing','unknown')",
            new { id, tenantId, providerRef, url, expiresAt = expiresAt.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PaymentEvent>> EventsAsync(string tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EventRow>(new CommandDefinition("""
            SELECT id AS Id, deposit_id AS DepositId, from_state AS FromState, to_state AS ToState, at AS At, source AS Source, detail AS Detail
            FROM quotes.payment_events WHERE tenant_id = @tenantId AND deposit_id = @id ORDER BY id
            """, new { tenantId, id }, cancellationToken: ct));
        return [.. rows.Select(r => new PaymentEvent(r.Id, r.DepositId, r.FromState, r.ToState, new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc)), r.Source, r.Detail))];
    }

    public async Task<IReadOnlyList<Deposit>> AwaitingAnswerAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.deposits WHERE status = 'unknown' OR (status = 'processing' AND checkout_expires_at < @now) ORDER BY created_at LIMIT @limit",
            new { now = now.UtcDateTime, limit }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<Deposit?> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.deposits WHERE tenant_id = @tenantId AND project_id = @projectId AND status = 'succeeded' AND kind = 'deposit' ORDER BY paid_at DESC LIMIT 1",
            new { tenantId, projectId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Deposit>> ListPaidAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.deposits WHERE tenant_id = @tenantId AND status = 'succeeded' AND kind = 'deposit' AND paid_at >= @from AND paid_at < @to ORDER BY paid_at",
            new { tenantId, from = from.UtcDateTime, to = to.UtcDateTime }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<Deposit?> BalanceForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.deposits WHERE tenant_id = @tenantId AND project_id = @projectId AND kind = 'balance' ORDER BY (status = 'succeeded') DESC, created_at DESC LIMIT 1",
            new { tenantId, projectId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    private static Task AppendAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, Guid depositId, string from, string to, DateTimeOffset at,
        string source, string? detail, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.payment_events(tenant_id, deposit_id, from_state, to_state, at, source, detail)
            VALUES (@tenantId, @depositId, @from, @to, @at, @source, @detail)
            """, new { tenantId, depositId, from, to, at = at.UtcDateTime, source, detail = detail is { Length: > 500 } ? detail[..500] : detail }, tx, cancellationToken: ct));

    private sealed class EventRow
    {
        public long Id { get; init; }
        public Guid DepositId { get; init; }
        public string FromState { get; init; } = "";
        public string ToState { get; init; } = "";
        public DateTime At { get; init; }
        public string Source { get; init; } = "";
        public string? Detail { get; init; }
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
        public string? ProviderRef { get; init; }
        public string? CheckoutUrl { get; init; }
        public DateTime? CheckoutExpiresAt { get; init; }
        public string Kind { get; init; } = PaymentKinds.Deposit;

        private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

        public Deposit ToDomain() => new(Id, TenantId, ProjectId, QuoteId, CustomerId, Amount, Currency.Trim(), Percent, Status, Provider,
            Utc(CreatedAt), PaidAt is DateTime p ? Utc(p) : null, PaidBy, ProviderRef, CheckoutUrl, CheckoutExpiresAt is DateTime e ? Utc(e) : null, Kind);
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryDepositStore : IDepositStore
{
    private readonly Lock _gate = new();
    private readonly List<Deposit> _rows = [];
    private readonly List<(string TenantId, PaymentEvent Event)> _events = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CreateAsync(Deposit deposit, CancellationToken ct)
    {
        lock (_gate)
        {
            _rows.Add(deposit);
            _events.Add((deposit.TenantId, new PaymentEvent(_events.Count + 1, deposit.Id, "", deposit.Status, deposit.CreatedAt, PaymentSources.Checkout, null)));
        }
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
            return Task.FromResult(_rows.Where(d => d.TenantId == tenantId && d.QuoteId == quoteId && d.Kind == PaymentKinds.Deposit)
                .OrderByDescending(d => d.Paid).ThenByDescending(d => d.CreatedAt).ThenByDescending(d => _rows.IndexOf(d)).FirstOrDefault());
        }
    }

    public Task<bool> TransitionAsync(string tenantId, Guid id, string from, string to, DateTimeOffset at, string source, string? detail, string? paidBy, CancellationToken ct)
    {
        if (!PaymentStates.CanMove(from, to)) throw new InvalidOperationException($"A payment can't move from {from} to {to}.");
        lock (_gate)
        {
            var i = _rows.FindIndex(d => d.Id == id && d.TenantId == tenantId && d.Status == from);
            if (i < 0) return Task.FromResult(false);
            _rows[i] = to == PaymentStates.Succeeded ? _rows[i] with { Status = to, PaidAt = at, PaidBy = paidBy } : _rows[i] with { Status = to };
            _events.Add((tenantId, new PaymentEvent(_events.Count + 1, id, from, to, at, source, detail)));
            return Task.FromResult(true);
        }
    }

    public Task SetCheckoutAsync(string tenantId, Guid id, string providerRef, string url, DateTimeOffset expiresAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _rows.FindIndex(d => d.Id == id && d.TenantId == tenantId && !PaymentStates.IsFinal(d.Status));
            if (i >= 0) _rows[i] = _rows[i] with { ProviderRef = providerRef, CheckoutUrl = url, CheckoutExpiresAt = expiresAt };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentEvent>> EventsAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PaymentEvent>>([.. _events.Where(e => e.TenantId == tenantId && e.Event.DepositId == id).Select(e => e.Event)]);
    }

    public Task<IReadOnlyList<Deposit>> AwaitingAnswerAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Deposit>>([.. _rows
                .Where(d => d.Status == PaymentStates.Unknown || (d.Status == PaymentStates.Processing && d.CheckoutExpiresAt < now)).Take(limit)]);
        }
    }

    public Task<Deposit?> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rows.Where(d => d.TenantId == tenantId && d.ProjectId == projectId && d.Paid && d.Kind == PaymentKinds.Deposit).OrderByDescending(d => d.PaidAt).FirstOrDefault());
    }

    public Task<IReadOnlyList<Deposit>> ListPaidAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Deposit>>([.. _rows
                .Where(d => d.TenantId == tenantId && d.Paid && d.Kind == PaymentKinds.Deposit && d.PaidAt >= from && d.PaidAt < to).OrderBy(d => d.PaidAt)]);
        }
    }

    public Task<Deposit?> BalanceForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Where(d => d.TenantId == tenantId && d.ProjectId == projectId && d.Kind == PaymentKinds.Balance)
                .OrderByDescending(d => d.Paid).ThenByDescending(d => d.CreatedAt).ThenByDescending(d => _rows.IndexOf(d)).FirstOrDefault());
        }
    }
}
