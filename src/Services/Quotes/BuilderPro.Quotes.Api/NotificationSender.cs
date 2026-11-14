using System.Net.Mail;
using BuilderPro.Quotes.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// Where to reach a customer: a snapshot of their profile, refreshed whenever THEY act with their own token
/// (book a visit, start a deposit, turn on calls). Identity owns the profile; this copy only delivers messages.
/// </summary>
public sealed record CustomerContact(string TenantId, string CustomerId, string? Email, string? Phone, string Hours, DateTimeOffset UpdatedAt);

public interface IContactStore
{
    Task InitializeAsync(CancellationToken ct);
    Task UpsertAsync(CustomerContact contact, CancellationToken ct);
    Task<CustomerContact?> FindAsync(string tenantId, string customerId, CancellationToken ct);
}

public static class ContactSnapshot
{
    /// <summary>Refreshes the snapshot from the customer's own profile. Best effort: a failure keeps the last one.</summary>
    public static async Task RefreshAsync(HttpContext http, string tenantId, string? customerId, CancellationToken ct)
    {
        if (customerId is null) return;
        var profiles = http.RequestServices.GetService<IProfileReader>();
        var contacts = http.RequestServices.GetService<IContactStore>();
        if (profiles is null || contacts is null) return;
        var p = await profiles.MineAsync(http, ct);
        if (p is null) return;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        await contacts.UpsertAsync(new CustomerContact(tenantId, customerId, p.Email, p.Phone, p.ContactHours, now), ct);
    }
}

/// <summary>Outbox rows waiting to go out, and the record of what happened to each.</summary>
public interface INotificationOutbox
{
    Task<IReadOnlyList<Notification>> UnsentAsync(int limit, CancellationToken ct);
    Task MarkAsync(Guid id, DateTimeOffset at, string result, CancellationToken ct);
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public interface ISmsSender
{
    string Name { get; }
    Task SendAsync(string to, string body, CancellationToken ct);
}

/// <summary>SMTP (Mailpit in development; a real relay in production via Notifications:Smtp:*).</summary>
public sealed class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(config["Notifications:Smtp:Host"] ?? "localhost", config.GetValue<int?>("Notifications:Smtp:Port") ?? 1025)
        {
            EnableSsl = config.GetValue("Notifications:Smtp:Ssl", false),
        };
        using var message = new MailMessage(config["Notifications:Smtp:From"] ?? "no-reply@builderpro.local", to, subject, body);
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>Records texts instead of sending them, until an SMS provider (ACS SMS / Twilio) is configured. Never logs the number.</summary>
public sealed class SimulatedSmsSender(ILogger<SimulatedSmsSender> log) : ISmsSender
{
    private readonly Lock _gate = new();
    private readonly List<(string To, string Body)> _sent = [];
    public string Name => "simulated";
    public IReadOnlyList<(string To, string Body)> Sent { get { lock (_gate) return [.. _sent]; } }

    public Task SendAsync(string to, string body, CancellationToken ct)
    {
        lock (_gate) _sent.Add((to, body));
        log.LogInformation("Simulated text sent ({Length} chars)", body.Length);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Delivers the order-update outbox (6c, 4b) every Notifications:SendSeconds (60):
///   email - any time, to the contact snapshot's email;
///   sms   - only inside the customer's contact hours (CallWindows), otherwise it waits for the next pass;
///   call  - not a message channel (automated calls are 4b's own flow): marked skipped.
/// No address on file: skipped with the reason. A send failure is retried on later passes (up to a day).
/// </summary>
public sealed class NotificationSender(IServiceScopeFactory scopes, IConfiguration config, ILogger<NotificationSender> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue<int?>("Notifications:SendSeconds") ?? 60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Notification pass failed"); }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var outbox = sp.GetRequiredService<INotificationOutbox>();
        var contacts = sp.GetRequiredService<IContactStore>();
        var email = sp.GetRequiredService<IEmailSender>();
        var sms = sp.GetRequiredService<ISmsSender>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        var sent = 0;

        foreach (var n in await outbox.UnsentAsync(100, ct))
        {
            var contact = await contacts.FindAsync(n.TenantId, n.CustomerId, ct);
            try
            {
                switch (n.Channel)
                {
                    case "email" when contact?.Email is { Length: > 0 } to:
                        await email.SendAsync(to, "Your BuilderPro project", n.Message, ct);
                        await outbox.MarkAsync(n.Id, now, "sent", ct);
                        sent++;
                        break;
                    case "sms" when contact?.Phone is { Length: > 0 } phone:
                        if (!CallWindows.MayCall(now, contact.Hours, CallEndpoints.StoreZone(config, n.TenantId))) continue;   // wait for their hours
                        await sms.SendAsync(phone, n.Message, ct);
                        await outbox.MarkAsync(n.Id, now, $"sent ({sms.Name})", ct);
                        sent++;
                        break;
                    case "call":
                        await outbox.MarkAsync(n.Id, now, "skipped: calls are placed by the scheduling flow only", ct);
                        break;
                    default:
                        await outbox.MarkAsync(n.Id, now, "skipped: no address on file for this channel", ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException or HttpRequestException)
            {
                log.LogWarning("Sending notification {Id} on {Channel} failed ({Error})", n.Id, n.Channel, ex.GetType().Name);
                if (now - n.CreatedAt > TimeSpan.FromDays(1)) await outbox.MarkAsync(n.Id, now, "failed after retries", ct);
            }
        }
        return sent;
    }
}

/// <summary>Contact snapshots and the outbox's send state in Postgres (quotes schema v12).</summary>
public sealed class PostgresContactStore(NpgsqlDataSource db) : IContactStore, INotificationOutbox
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            -- v12 (final gaps): where to reach a customer, and what happened to each outbox message.
            CREATE TABLE IF NOT EXISTS quotes.customer_contacts(
                tenant_id text NOT NULL,
                customer_id text NOT NULL,
                email text NULL,
                phone text NULL,
                hours text NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (tenant_id, customer_id));
            CREATE TABLE IF NOT EXISTS quotes.notifications(
                id uuid PRIMARY KEY, tenant_id text NOT NULL, project_id uuid NOT NULL, customer_id text NOT NULL,
                channel text NOT NULL, message text NOT NULL, created_at timestamptz NOT NULL, sent_at timestamptz NULL);
            ALTER TABLE quotes.notifications ADD COLUMN IF NOT EXISTS result text NULL;
            INSERT INTO quotes.schema_history(version) VALUES (12) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task UpsertAsync(CustomerContact c, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.customer_contacts(tenant_id, customer_id, email, phone, hours, updated_at)
            VALUES (@TenantId, @CustomerId, @Email, @Phone, @Hours, @UpdatedAt)
            ON CONFLICT (tenant_id, customer_id) DO UPDATE SET email = EXCLUDED.email, phone = EXCLUDED.phone, hours = EXCLUDED.hours, updated_at = EXCLUDED.updated_at
            """, new { c.TenantId, c.CustomerId, c.Email, c.Phone, c.Hours, UpdatedAt = c.UpdatedAt.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<CustomerContact?> FindAsync(string tenantId, string customerId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string TenantId, string CustomerId, string? Email, string? Phone, string Hours, DateTime UpdatedAt)?>(new CommandDefinition(
            "SELECT tenant_id, customer_id, email, phone, hours, updated_at FROM quotes.customer_contacts WHERE tenant_id = @tenantId AND customer_id = @customerId",
            new { tenantId, customerId }, cancellationToken: ct));
        return row is { } r ? new CustomerContact(r.TenantId, r.CustomerId, r.Email, r.Phone, r.Hours, new DateTimeOffset(DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc))) : null;
    }

    public async Task<IReadOnlyList<Notification>> UnsentAsync(int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, string TenantId, Guid ProjectId, string CustomerId, string Channel, string Message, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT id, tenant_id, project_id, customer_id, channel, message, created_at FROM quotes.notifications WHERE sent_at IS NULL ORDER BY created_at LIMIT @limit",
            new { limit }, cancellationToken: ct));
        return [.. rows.Select(r => new Notification(r.Id, r.TenantId, r.ProjectId, r.CustomerId, r.Channel, r.Message, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc))))];
    }

    public async Task MarkAsync(Guid id, DateTimeOffset at, string result, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE quotes.notifications SET sent_at = @at, result = @result WHERE id = @id AND sent_at IS NULL",
            new { id, at = at.UtcDateTime, result }, cancellationToken: ct));
    }
}

/// <summary>For tests only: contacts, plus an outbox view over the in-memory install store's notifications.</summary>
public sealed class InMemoryContactStore(InMemoryInstallStore installs) : IContactStore, INotificationOutbox
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, string), CustomerContact> _contacts = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string Result)> _marked = [];

    public IReadOnlyDictionary<Guid, (DateTimeOffset At, string Result)> Marked { get { lock (_gate) return new Dictionary<Guid, (DateTimeOffset, string)>(_marked); } }

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task UpsertAsync(CustomerContact contact, CancellationToken ct)
    {
        lock (_gate) _contacts[(contact.TenantId, contact.CustomerId)] = contact;
        return Task.CompletedTask;
    }

    public Task<CustomerContact?> FindAsync(string tenantId, string customerId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_contacts.GetValueOrDefault((tenantId, customerId)));
    }

    public Task<IReadOnlyList<Notification>> UnsentAsync(int limit, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Notification>>([.. installs.AllNotifications.Where(n => !_marked.ContainsKey(n.Id)).Take(limit)]);
    }

    public Task MarkAsync(Guid id, DateTimeOffset at, string result, CancellationToken ct)
    {
        lock (_gate) _marked.TryAdd(id, (at, result));
        return Task.CompletedTask;
    }
}
