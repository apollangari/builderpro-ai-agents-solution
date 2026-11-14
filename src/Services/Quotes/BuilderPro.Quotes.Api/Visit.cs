using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api;

/// <summary>6b: the measure visit booked for a project (one active booking per project).</summary>
public sealed record Visit(
    Guid Id, string TenantId, Guid ProjectId, string CustomerId, DateTimeOffset Start, string Status,
    string BookedBy, DateTimeOffset BookedAt, string? MeasuredBy = null, DateTimeOffset? MeasuredAt = null);

public static class VisitStatus
{
    public const string Booked = "booked";
    public const string Measured = "measured";
    public const string Cancelled = "cancelled";
}

public interface IVisitStore
{
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Books the visit if the slot has room (fewer than perSlot bookings at that start in this tenant).
    /// Any earlier booking of the same project is cancelled in the same step (a reschedule). False: slot full.
    /// </summary>
    Task<bool> BookAsync(Visit visit, int perSlot, CancellationToken ct);

    /// <summary>The project's booked or measured visit (not cancelled), or null.</summary>
    Task<Visit?> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct);

    /// <summary>How many bookings each start has, in [from, to).</summary>
    Task<IReadOnlyDictionary<DateTimeOffset, int>> BookedAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>booked -> measured, once.</summary>
    Task<bool> MarkMeasuredAsync(string tenantId, Guid id, string by, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Visits in Postgres (quotes schema v7).</summary>
public sealed class PostgresVisitStore(NpgsqlDataSource db) : IVisitStore
{
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, project_id AS ProjectId, customer_id AS CustomerId, start_at AS StartAt, status AS Status,
        booked_by AS BookedBy, booked_at AS BookedAt, measured_by AS MeasuredBy, measured_at AS MeasuredAt
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v7 (6b): measure visits. One active (booked or measured) visit per project.
            CREATE TABLE IF NOT EXISTS quotes.visits(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                project_id uuid NOT NULL,
                customer_id text NOT NULL,
                start_at timestamptz NOT NULL,
                status text NOT NULL CHECK (status IN ('booked','measured','cancelled')),
                booked_by text NOT NULL,
                booked_at timestamptz NOT NULL,
                measured_by text NULL,
                measured_at timestamptz NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_visits_active ON quotes.visits(project_id) WHERE status <> 'cancelled';
            CREATE INDEX IF NOT EXISTS ix_visits_slot ON quotes.visits(tenant_id, start_at) WHERE status = 'booked';
            INSERT INTO quotes.schema_history(version) VALUES (7) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task<bool> BookAsync(Visit v, int perSlot, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // One booking decision per tenant + slot at a time, so two customers can't both take the last place.
        await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@key))",
            new { key = $"visit:{v.TenantId}:{v.Start.UtcDateTime:O}" }, tx, cancellationToken: ct));
        var taken = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM quotes.visits WHERE tenant_id = @TenantId AND start_at = @Start AND status = 'booked' AND project_id <> @ProjectId",
            new { v.TenantId, Start = v.Start.UtcDateTime, v.ProjectId }, tx, cancellationToken: ct));
        if (taken >= perSlot) return false;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE quotes.visits SET status = 'cancelled' WHERE tenant_id = @TenantId AND project_id = @ProjectId AND status = 'booked'",
            new { v.TenantId, v.ProjectId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.visits(id, tenant_id, project_id, customer_id, start_at, status, booked_by, booked_at)
            VALUES (@Id, @TenantId, @ProjectId, @CustomerId, @Start, 'booked', @BookedBy, @BookedAt)
            """, new { v.Id, v.TenantId, v.ProjectId, v.CustomerId, Start = v.Start.UtcDateTime, v.BookedBy, BookedAt = v.BookedAt.UtcDateTime },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<Visit?> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.visits WHERE tenant_id = @tenantId AND project_id = @projectId AND status <> 'cancelled' LIMIT 1",
            new { tenantId, projectId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyDictionary<DateTimeOffset, int>> BookedAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(DateTime Start, long Count)>(new CommandDefinition(
            "SELECT start_at, count(*) FROM quotes.visits WHERE tenant_id = @tenantId AND status = 'booked' AND start_at >= @from AND start_at < @to GROUP BY start_at",
            new { tenantId, from = from.UtcDateTime, to = to.UtcDateTime }, cancellationToken: ct));
        return rows.ToDictionary(r => new DateTimeOffset(DateTime.SpecifyKind(r.Start, DateTimeKind.Utc)), r => (int)r.Count);
    }

    public async Task<bool> MarkMeasuredAsync(string tenantId, Guid id, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE quotes.visits SET status = 'measured', measured_by = @by, measured_at = @at WHERE id = @id AND tenant_id = @tenantId AND status = 'booked'",
            new { id, tenantId, by, at = at.UtcDateTime }, cancellationToken: ct)) == 1;
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public Guid ProjectId { get; init; }
        public string CustomerId { get; init; } = "";
        public DateTime StartAt { get; init; }
        public string Status { get; init; } = "";
        public string BookedBy { get; init; } = "";
        public DateTime BookedAt { get; init; }
        public string? MeasuredBy { get; init; }
        public DateTime? MeasuredAt { get; init; }

        private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

        public Visit ToDomain() => new(Id, TenantId, ProjectId, CustomerId, Utc(StartAt), Status, BookedBy, Utc(BookedAt),
            MeasuredBy, MeasuredAt is DateTime m ? Utc(m) : null);
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryVisitStore : IVisitStore
{
    private readonly Lock _gate = new();
    private readonly List<Visit> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> BookAsync(Visit v, int perSlot, CancellationToken ct)
    {
        lock (_gate)
        {
            var taken = _rows.Count(x => x.TenantId == v.TenantId && x.Start == v.Start && x.Status == VisitStatus.Booked && x.ProjectId != v.ProjectId);
            if (taken >= perSlot) return Task.FromResult(false);
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].TenantId == v.TenantId && _rows[i].ProjectId == v.ProjectId && _rows[i].Status == VisitStatus.Booked)
                    _rows[i] = _rows[i] with { Status = VisitStatus.Cancelled };
            }
            _rows.Add(v with { Status = VisitStatus.Booked });
            return Task.FromResult(true);
        }
    }

    public Task<Visit?> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rows.FirstOrDefault(x => x.TenantId == tenantId && x.ProjectId == projectId && x.Status != VisitStatus.Cancelled));
    }

    public Task<IReadOnlyDictionary<DateTimeOffset, int>> BookedAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<DateTimeOffset, int>>(_rows
                .Where(x => x.TenantId == tenantId && x.Status == VisitStatus.Booked && x.Start >= from && x.Start < to)
                .GroupBy(x => x.Start).ToDictionary(g => g.Key, g => g.Count()));
        }
    }

    public Task<bool> MarkMeasuredAsync(string tenantId, Guid id, string by, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _rows.FindIndex(x => x.Id == id && x.TenantId == tenantId && x.Status == VisitStatus.Booked);
            if (i < 0) return Task.FromResult(false);
            _rows[i] = _rows[i] with { Status = VisitStatus.Measured, MeasuredBy = by, MeasuredAt = at };
            return Task.FromResult(true);
        }
    }
}
