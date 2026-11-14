using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuilderPro.Security;
using Dapper;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace BuilderPro.Quotes.Api;

/// <summary>6c: one step of the installation, as staff recorded it.</summary>
public sealed record InstallEvent(Guid Id, string TenantId, Guid ProjectId, string Step, DateTimeOffset At, string By, DateTimeOffset? ScheduledFor, string? Note);

/// <summary>The installation steps, in order. Staff advance them one at a time ("scheduled" may be repeated to move the date).</summary>
public static class InstallSteps
{
    public const string Scheduled = "scheduled";
    public const string Fabricating = "fabricating";
    public const string Installing = "installing";
    public const string Done = "done";
    public static readonly string[] All = [Scheduled, Fabricating, Installing, Done];

    /// <summary>May the job go from <paramref name="current"/> (null = nothing yet) to <paramref name="next"/>?</summary>
    public static bool CanMove(string? current, string next) =>
        current is null ? next == Scheduled
        : next == Scheduled ? current == Scheduled                       // reschedule before work starts
        : Array.IndexOf(All, next) == Array.IndexOf(All, current) + 1;
}

/// <summary>An order update recorded for the customer on one channel they consented to (sent later by a notifier).</summary>
public sealed record Notification(Guid Id, string TenantId, Guid ProjectId, string CustomerId, string Channel, string Message, DateTimeOffset CreatedAt);

public interface IInstallStore
{
    Task InitializeAsync(CancellationToken ct);
    Task AddAsync(InstallEvent e, IReadOnlyList<Notification> notifications, CancellationToken ct);
    /// <summary>4b: messages that aren't an install step (e.g. a visit confirm link after a scheduling call).</summary>
    Task AddNotificationsAsync(IReadOnlyList<Notification> notifications, CancellationToken ct);
    Task<IReadOnlyList<InstallEvent>> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<Notification>> NotificationsAsync(string tenantId, Guid projectId, CancellationToken ct);
}

/// <summary>Install events and the notification outbox in Postgres (quotes schema v8). Written in one transaction.</summary>
public sealed class PostgresInstallStore(NpgsqlDataSource db) : IInstallStore
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v8 (6c): installation steps and the order-update outbox (consented channels only).
            CREATE TABLE IF NOT EXISTS quotes.install_events(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                project_id uuid NOT NULL,
                step text NOT NULL CHECK (step IN ('scheduled','fabricating','installing','done')),
                at timestamptz NOT NULL,
                by_user text NOT NULL,
                scheduled_for timestamptz NULL,
                note text NULL);
            CREATE INDEX IF NOT EXISTS ix_install_project ON quotes.install_events(tenant_id, project_id, at);
            CREATE TABLE IF NOT EXISTS quotes.notifications(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                project_id uuid NOT NULL,
                customer_id text NOT NULL,
                channel text NOT NULL CHECK (channel IN ('email','sms','call')),
                message text NOT NULL,
                created_at timestamptz NOT NULL,
                sent_at timestamptz NULL);
            CREATE INDEX IF NOT EXISTS ix_notifications_unsent ON quotes.notifications(created_at) WHERE sent_at IS NULL;
            INSERT INTO quotes.schema_history(version) VALUES (8) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task AddAsync(InstallEvent e, IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.install_events(id, tenant_id, project_id, step, at, by_user, scheduled_for, note)
            VALUES (@Id, @TenantId, @ProjectId, @Step, @At, @By, @ScheduledFor, @Note)
            """, new { e.Id, e.TenantId, e.ProjectId, e.Step, At = e.At.UtcDateTime, e.By, ScheduledFor = e.ScheduledFor?.UtcDateTime, e.Note }, tx, cancellationToken: ct));
        foreach (var n in notifications)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO quotes.notifications(id, tenant_id, project_id, customer_id, channel, message, created_at)
                VALUES (@Id, @TenantId, @ProjectId, @CustomerId, @Channel, @Message, @CreatedAt)
                """, new { n.Id, n.TenantId, n.ProjectId, n.CustomerId, n.Channel, n.Message, CreatedAt = n.CreatedAt.UtcDateTime }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task AddNotificationsAsync(IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        foreach (var n in notifications)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO quotes.notifications(id, tenant_id, project_id, customer_id, channel, message, created_at)
                VALUES (@Id, @TenantId, @ProjectId, @CustomerId, @Channel, @Message, @CreatedAt)
                """, new { n.Id, n.TenantId, n.ProjectId, n.CustomerId, n.Channel, n.Message, CreatedAt = n.CreatedAt.UtcDateTime }, cancellationToken: ct));
        }
    }

    public async Task<IReadOnlyList<InstallEvent>> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EventRow>(new CommandDefinition("""
            SELECT id AS Id, tenant_id AS TenantId, project_id AS ProjectId, step AS Step, at AS At, by_user AS By, scheduled_for AS ScheduledFor, note AS Note
            FROM quotes.install_events WHERE tenant_id = @tenantId AND project_id = @projectId ORDER BY at, id
            """, new { tenantId, projectId }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<Notification>> NotificationsAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<NotificationRow>(new CommandDefinition("""
            SELECT id AS Id, tenant_id AS TenantId, project_id AS ProjectId, customer_id AS CustomerId, channel AS Channel, message AS Message, created_at AS CreatedAt
            FROM quotes.notifications WHERE tenant_id = @tenantId AND project_id = @projectId ORDER BY created_at, channel
            """, new { tenantId, projectId }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private sealed class EventRow
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public Guid ProjectId { get; init; }
        public string Step { get; init; } = "";
        public DateTime At { get; init; }
        public string By { get; init; } = "";
        public DateTime? ScheduledFor { get; init; }
        public string? Note { get; init; }
        public InstallEvent ToDomain() => new(Id, TenantId, ProjectId, Step, Utc(At), By, ScheduledFor is DateTime s ? Utc(s) : null, Note);
    }

    private sealed class NotificationRow
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public Guid ProjectId { get; init; }
        public string CustomerId { get; init; } = "";
        public string Channel { get; init; } = "";
        public string Message { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public Notification ToDomain() => new(Id, TenantId, ProjectId, CustomerId, Channel, Message, Utc(CreatedAt));
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryInstallStore : IInstallStore
{
    private readonly Lock _gate = new();
    private readonly List<InstallEvent> _events = [];
    private readonly List<Notification> _notifications = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task AddAsync(InstallEvent e, IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        lock (_gate)
        {
            _events.Add(e);
            _notifications.AddRange(notifications);
        }
        return Task.CompletedTask;
    }

    public Task AddNotificationsAsync(IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        lock (_gate) _notifications.AddRange(notifications);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstallEvent>> ForProjectAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<InstallEvent>>([.. _events.Where(e => e.TenantId == tenantId && e.ProjectId == projectId)]);
    }

    public Task<IReadOnlyList<Notification>> NotificationsAsync(string tenantId, Guid projectId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Notification>>([.. _notifications.Where(n => n.TenantId == tenantId && n.ProjectId == projectId)]);
    }
}

/// <summary>Which channels the customer consented to for order updates, asked from Identity with the staff member's token.</summary>
public interface IContactChannels
{
    /// <summary>The consented channels, or null when Identity couldn't answer.</summary>
    Task<IReadOnlyList<string>?> ForCustomerAsync(HttpContext http, string customerId, CancellationToken ct);
}

public sealed class IdentityContactChannels(HttpClient client, ILogger<IdentityContactChannels> log) : IContactChannels
{
    private sealed record Answer(string CustomerId, string[] Channels, string Hours);

    public async Task<IReadOnlyList<string>?> ForCustomerAsync(HttpContext http, string customerId, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(http.Request.Headers[HeaderNames.Authorization], out var auth) || auth.Parameter is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/identity/customers/{Uri.EscapeDataString(customerId)}/channels");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Parameter);
        var hint = http.Request.Headers[TenantHint.Header].ToString();
        if (ClaimsNormalizer.IsIdentifier(hint)) request.Headers.Add(TenantHint.Header, hint);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<Answer>(ct))?.Channels;
            log.LogWarning("Contact channels answered {Status}", (int)response.StatusCode);
            return response.StatusCode == HttpStatusCode.NotFound ? [] : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or System.Text.Json.JsonException)
        {
            log.LogWarning(ex, "Contact channels lookup failed");
            return null;
        }
    }
}
