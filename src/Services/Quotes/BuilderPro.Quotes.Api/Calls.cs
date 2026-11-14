using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// 4b: the customer's AUTOMATED-CALL consent: separate from the ordinary contact consent, recorded with the
/// exact wording version they agreed to, append-only (the history is the audit trail). The latest row counts.
/// Phone, contact hours and message channels are a snapshot taken from their profile when they opted in.
/// </summary>
public sealed record CallConsent(
    long Id, string TenantId, string CustomerId, bool Granted, string? Phone, string Hours, string Channels,
    string WordingVersion, string Source, DateTimeOffset At);

/// <summary>One scheduling call to place (or placed) for a project.</summary>
public sealed record CallRequest(
    Guid Id, string TenantId, Guid ProjectId, string CustomerId, string Status, DateTimeOffset DueAt, int Attempts,
    string? ProposalsJson, DateTimeOffset? ChosenStart, string? ProviderRef, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public static class CallStatus
{
    public const string Scheduled = "scheduled";            // waiting for an allowed time
    public const string Calling = "calling";                // placed; waiting for the provider's events
    public const string SlotChosen = "slot_chosen";         // the customer picked a time: a confirm link was sent
    public const string PersonRequested = "person_requested"; // the customer asked for a person: staff follow up
    public const string OptedOut = "opted_out";             // the customer stopped automated calls
    public const string NoAnswer = "no_answer";             // no answer after the last attempt
    public const string Cancelled = "cancelled";            // no longer needed (booked, consent withdrawn, stage moved on)
    public static readonly string[] Open = [Scheduled, Calling];
}

public interface ICallStore
{
    Task InitializeAsync(CancellationToken ct);
    Task AppendConsentAsync(CallConsent consent, CancellationToken ct);
    Task<CallConsent?> LatestConsentAsync(string tenantId, string customerId, CancellationToken ct);
    Task CreateAsync(CallRequest call, CancellationToken ct);
    Task UpdateAsync(CallRequest call, CancellationToken ct);
    /// <summary>By id only: the telephony provider's callback carries no tenant (its signature authenticates it).</summary>
    Task<CallRequest?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<CallRequest>> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct);
    /// <summary>Scheduled calls whose time has come (any tenant: the dispatcher's work list).</summary>
    Task<IReadOnlyList<CallRequest>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct);
    /// <summary>Opt-out: every open call of this customer is cancelled at once.</summary>
    Task<int> CancelOpenForCustomerAsync(string tenantId, string customerId, string status, string note, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Calls in Postgres (quotes schema v11).</summary>
public sealed class PostgresCallStore(NpgsqlDataSource db) : ICallStore
{
    private const string CallColumns = """
        id AS Id, tenant_id AS TenantId, project_id AS ProjectId, customer_id AS CustomerId, status AS Status, due_at AS DueAt,
        attempts AS Attempts, proposals::text AS ProposalsJson, chosen_start AS ChosenStart, provider_ref AS ProviderRef, note AS Note,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v11 (4b): automated-call consent (append-only) and scheduling calls.
            CREATE TABLE IF NOT EXISTS quotes.call_consents(
                id bigserial PRIMARY KEY,
                tenant_id text NOT NULL,
                customer_id text NOT NULL,
                granted boolean NOT NULL,
                phone text NULL,
                hours text NOT NULL,
                channels text NOT NULL,
                wording_version text NOT NULL,
                source text NOT NULL,
                at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_call_consents_customer ON quotes.call_consents(tenant_id, customer_id, id DESC);
            CREATE TABLE IF NOT EXISTS quotes.calls(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                project_id uuid NOT NULL,
                customer_id text NOT NULL,
                status text NOT NULL CHECK (status IN ('scheduled','calling','slot_chosen','person_requested','opted_out','no_answer','cancelled')),
                due_at timestamptz NOT NULL,
                attempts integer NOT NULL DEFAULT 0,
                proposals jsonb NULL,
                chosen_start timestamptz NULL,
                provider_ref text NULL,
                note text NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_calls_open ON quotes.calls(project_id) WHERE status IN ('scheduled','calling');
            CREATE INDEX IF NOT EXISTS ix_calls_due ON quotes.calls(due_at) WHERE status = 'scheduled';
            INSERT INTO quotes.schema_history(version) VALUES (11) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task AppendConsentAsync(CallConsent c, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.call_consents(tenant_id, customer_id, granted, phone, hours, channels, wording_version, source, at)
            VALUES (@TenantId, @CustomerId, @Granted, @Phone, @Hours, @Channels, @WordingVersion, @Source, @At)
            """, new { c.TenantId, c.CustomerId, c.Granted, c.Phone, c.Hours, c.Channels, c.WordingVersion, c.Source, At = c.At.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<CallConsent?> LatestConsentAsync(string tenantId, string customerId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ConsentRow>(new CommandDefinition("""
            SELECT id AS Id, tenant_id AS TenantId, customer_id AS CustomerId, granted AS Granted, phone AS Phone, hours AS Hours, channels AS Channels,
                   wording_version AS WordingVersion, source AS Source, at AS At
            FROM quotes.call_consents WHERE tenant_id = @tenantId AND customer_id = @customerId ORDER BY id DESC LIMIT 1
            """, new { tenantId, customerId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task CreateAsync(CallRequest c, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.calls(id, tenant_id, project_id, customer_id, status, due_at, attempts, proposals, chosen_start, provider_ref, note, created_at, updated_at)
            VALUES (@Id, @TenantId, @ProjectId, @CustomerId, @Status, @DueAt, @Attempts, @ProposalsJson::jsonb, @ChosenStart, @ProviderRef, @Note, @CreatedAt, @UpdatedAt)
            """, Args(c), cancellationToken: ct));
    }

    public async Task UpdateAsync(CallRequest c, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE quotes.calls SET status = @Status, due_at = @DueAt, attempts = @Attempts, proposals = @ProposalsJson::jsonb,
                   chosen_start = @ChosenStart, provider_ref = @ProviderRef, note = @Note, updated_at = @UpdatedAt
            WHERE id = @Id AND tenant_id = @TenantId
            """, Args(c), cancellationToken: ct));
    }

    public async Task<CallRequest?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QuerySingleOrDefaultAsync<CallRow>(new CommandDefinition($"SELECT {CallColumns} FROM quotes.calls WHERE id = @id", new { id }, cancellationToken: ct)))?.ToDomain();
    }

    public async Task<IReadOnlyList<CallRequest>> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CallRow>(new CommandDefinition(
            $"SELECT {CallColumns} FROM quotes.calls WHERE tenant_id = @tenantId AND project_id = @projectId ORDER BY created_at", new { tenantId, projectId }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<CallRequest>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CallRow>(new CommandDefinition(
            $"SELECT {CallColumns} FROM quotes.calls WHERE status = 'scheduled' AND due_at <= @now ORDER BY due_at LIMIT @limit", new { now = now.UtcDateTime, limit }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<int> CancelOpenForCustomerAsync(string tenantId, string customerId, string status, string note, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE quotes.calls SET status = @status, note = @note, updated_at = @at WHERE tenant_id = @tenantId AND customer_id = @customerId AND status IN ('scheduled','calling')",
            new { tenantId, customerId, status, note, at = at.UtcDateTime }, cancellationToken: ct));
    }

    private static object Args(CallRequest c) => new
    {
        c.Id, c.TenantId, c.ProjectId, c.CustomerId, c.Status, DueAt = c.DueAt.UtcDateTime, c.Attempts, c.ProposalsJson,
        ChosenStart = c.ChosenStart?.UtcDateTime, c.ProviderRef, c.Note, CreatedAt = c.CreatedAt.UtcDateTime, UpdatedAt = c.UpdatedAt.UtcDateTime,
    };

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private sealed class ConsentRow
    {
        public long Id { get; init; }
        public string TenantId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public bool Granted { get; init; }
        public string? Phone { get; init; }
        public string Hours { get; init; } = "";
        public string Channels { get; init; } = "";
        public string WordingVersion { get; init; } = "";
        public string Source { get; init; } = "";
        public DateTime At { get; init; }
        public CallConsent ToDomain() => new(Id, TenantId, CustomerId, Granted, Phone, Hours, Channels, WordingVersion, Source, Utc(At));
    }

    private sealed class CallRow
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public Guid ProjectId { get; init; }
        public string CustomerId { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime DueAt { get; init; }
        public int Attempts { get; init; }
        public string? ProposalsJson { get; init; }
        public DateTime? ChosenStart { get; init; }
        public string? ProviderRef { get; init; }
        public string? Note { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public CallRequest ToDomain() => new(Id, TenantId, ProjectId, CustomerId, Status, Utc(DueAt), Attempts, ProposalsJson,
            ChosenStart is DateTime c ? Utc(c) : null, ProviderRef, Note, Utc(CreatedAt), Utc(UpdatedAt));
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryCallStore : ICallStore
{
    private readonly Lock _gate = new();
    private readonly List<CallConsent> _consents = [];
    private readonly List<CallRequest> _calls = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task AppendConsentAsync(CallConsent consent, CancellationToken ct)
    {
        lock (_gate) _consents.Add(consent with { Id = _consents.Count + 1 });
        return Task.CompletedTask;
    }

    public Task<CallConsent?> LatestConsentAsync(string tenantId, string customerId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_consents.LastOrDefault(c => c.TenantId == tenantId && c.CustomerId == customerId));
    }

    public IReadOnlyList<CallConsent> Consents { get { lock (_gate) return [.. _consents]; } }

    public Task CreateAsync(CallRequest call, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_calls.Any(c => c.ProjectId == call.ProjectId && CallStatus.Open.Contains(c.Status)))
                throw new InvalidOperationException("one open call per project");
            _calls.Add(call);
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CallRequest call, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _calls.FindIndex(c => c.Id == call.Id && c.TenantId == call.TenantId);
            if (i >= 0) _calls[i] = call;
        }
        return Task.CompletedTask;
    }

    public Task<CallRequest?> FindAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_calls.FirstOrDefault(c => c.Id == id));
    }

    public Task<IReadOnlyList<CallRequest>> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CallRequest>>([.. _calls.Where(c => c.TenantId == tenantId && c.ProjectId == projectId)]);
    }

    public Task<IReadOnlyList<CallRequest>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CallRequest>>([.. _calls.Where(c => c.Status == CallStatus.Scheduled && c.DueAt <= now).OrderBy(c => c.DueAt).Take(limit)]);
    }

    public Task<int> CancelOpenForCustomerAsync(string tenantId, string customerId, string status, string note, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var n = 0;
            for (var i = 0; i < _calls.Count; i++)
            {
                if (_calls[i].TenantId != tenantId || _calls[i].CustomerId != customerId || !CallStatus.Open.Contains(_calls[i].Status)) continue;
                _calls[i] = _calls[i] with { Status = status, Note = note, UpdatedAt = at };
                n++;
            }
            return Task.FromResult(n);
        }
    }
}

/// <summary>What the call says and where the provider reports back.</summary>
public sealed record CallPlacement(Guid CallId, string Phone, string Script, IReadOnlyList<DateTimeOffset> Options, string CallbackUrl);

/// <summary>
/// 4b: the telephony seam. "simulated" (development and tests) records the call; a real provider (Azure
/// Communication Services Call Automation, or Twilio) places it and reports keypresses to the signed
/// callback. Throws on failure; the dispatcher retries at the next allowed time.
/// </summary>
public interface ITelephony
{
    string Name { get; }
    Task<string> PlaceCallAsync(CallPlacement placement, CancellationToken ct);
}

public sealed class SimulatedTelephony(ILogger<SimulatedTelephony> log) : ITelephony
{
    private readonly Lock _gate = new();
    private readonly List<CallPlacement> _placed = [];

    public string Name => "simulated";
    public IReadOnlyList<CallPlacement> Placed { get { lock (_gate) return [.. _placed]; } }

    public Task<string> PlaceCallAsync(CallPlacement placement, CancellationToken ct)
    {
        lock (_gate) _placed.Add(placement);
        log.LogInformation("Simulated scheduling call {Call} placed (no phone number logged)", placement.CallId);
        return Task.FromResult($"sim_call_{placement.CallId:N}");
    }
}
