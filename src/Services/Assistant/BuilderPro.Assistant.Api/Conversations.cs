using Dapper;
using Npgsql;

namespace BuilderPro.Assistant.Api;

/// <summary>
/// 3e: a customer's chat with the store. "concierge" = the AI answers; "staff" = a named person took over and
/// the AI is paused until they hand it back. Messages are append-only, numbered per conversation (Seq), so a
/// page asks only for what's new (after=Seq).
/// </summary>
public sealed record Conversation(
    Guid Id, string TenantId, string CustomerId, Guid? ProjectId, string Mode, string? StaffKey, string? StaffName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ConversationMessage(long Seq, Guid ConversationId, string Role, string Text, string? Author, DateTimeOffset At);

public static class ConversationModes
{
    public const string Concierge = "concierge";
    public const string Staff = "staff";
}

public static class MessageRoles
{
    public const string Customer = "customer";
    public const string Concierge = "concierge";
    public const string Staff = "staff";
    public const string System = "system";   // "Sam from the store joined", "The concierge is back"
}

public interface IConversationStore
{
    Task InitializeAsync(CancellationToken ct);
    Task CreateAsync(Conversation conversation, CancellationToken ct);
    /// <summary>One conversation of ONE tenant; with a customerId, only that customer's.</summary>
    Task<Conversation?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct);
    /// <summary>The tenant's conversations active since `since`, newest first.</summary>
    Task<IReadOnlyList<Conversation>> ListAsync(string tenantId, DateTimeOffset since, int limit, CancellationToken ct);
    Task<long> AppendAsync(string tenantId, Guid conversationId, string role, string text, string? author, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<ConversationMessage>> MessagesAsync(string tenantId, Guid conversationId, long after, int limit, CancellationToken ct);
    /// <summary>Switches who answers. Only from `from` (so two staff can't both take over). False if it wasn't in `from`.</summary>
    Task<bool> SetModeAsync(string tenantId, Guid id, string from, string to, string? staffKey, string? staffName, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Conversations in Postgres (assistant schema v1): the Assistant's own database.</summary>
public sealed class PostgresConversationStore(NpgsqlDataSource db) : IConversationStore
{
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, customer_id AS CustomerId, project_id AS ProjectId, mode AS Mode, staff_key AS StaffKey,
        staff_name AS StaffName, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS assistant;
            CREATE TABLE IF NOT EXISTS assistant.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v1 (3e): conversations and their messages.
            CREATE TABLE IF NOT EXISTS assistant.conversations(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                customer_id text NOT NULL,
                project_id uuid NULL,
                mode text NOT NULL CHECK (mode IN ('concierge','staff')),
                staff_key text NULL,
                staff_name text NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_conversations_recent ON assistant.conversations(tenant_id, updated_at DESC);
            CREATE TABLE IF NOT EXISTS assistant.messages(
                seq bigserial PRIMARY KEY,
                tenant_id text NOT NULL,
                conversation_id uuid NOT NULL REFERENCES assistant.conversations(id),
                role text NOT NULL CHECK (role IN ('customer','concierge','staff','system')),
                text text NOT NULL,
                author text NULL,
                at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_messages_conversation ON assistant.messages(conversation_id, seq);
            INSERT INTO assistant.schema_history(version) VALUES (1) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task CreateAsync(Conversation c, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO assistant.conversations(id, tenant_id, customer_id, project_id, mode, staff_key, staff_name, created_at, updated_at)
            VALUES (@Id, @TenantId, @CustomerId, @ProjectId, @Mode, @StaffKey, @StaffName, @CreatedAt, @UpdatedAt)
            """, new { c.Id, c.TenantId, c.CustomerId, c.ProjectId, c.Mode, c.StaffKey, c.StaffName, CreatedAt = c.CreatedAt.UtcDateTime, UpdatedAt = c.UpdatedAt.UtcDateTime },
            cancellationToken: ct));
    }

    public async Task<Conversation?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM assistant.conversations WHERE id = @id AND tenant_id = @tenantId AND (@customerId::text IS NULL OR customer_id = @customerId)",
            new { id, tenantId, customerId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(string tenantId, DateTimeOffset since, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM assistant.conversations WHERE tenant_id = @tenantId AND updated_at >= @since ORDER BY updated_at DESC LIMIT @limit",
            new { tenantId, since = since.UtcDateTime, limit }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<long> AppendAsync(string tenantId, Guid conversationId, string role, string text, string? author, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var seq = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO assistant.messages(tenant_id, conversation_id, role, text, author, at)
            SELECT @tenantId, id, @role, @text, @author, @at FROM assistant.conversations WHERE id = @conversationId AND tenant_id = @tenantId
            RETURNING seq
            """, new { tenantId, conversationId, role, text, author, at = at.UtcDateTime }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("UPDATE assistant.conversations SET updated_at = @at WHERE id = @conversationId AND tenant_id = @tenantId",
            new { conversationId, tenantId, at = at.UtcDateTime }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return seq;
    }

    public async Task<IReadOnlyList<ConversationMessage>> MessagesAsync(string tenantId, Guid conversationId, long after, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MessageRow>(new CommandDefinition("""
            SELECT seq AS Seq, conversation_id AS ConversationId, role AS Role, text AS Text, author AS Author, at AS At
            FROM assistant.messages WHERE tenant_id = @tenantId AND conversation_id = @conversationId AND seq > @after ORDER BY seq LIMIT @limit
            """, new { tenantId, conversationId, after, limit }, cancellationToken: ct));
        return [.. rows.Select(r => new ConversationMessage(r.Seq, r.ConversationId, r.Role, r.Text, r.Author, new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc))))];
    }

    public async Task<bool> SetModeAsync(string tenantId, Guid id, string from, string to, string? staffKey, string? staffName, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE assistant.conversations SET mode = @to, staff_key = @staffKey, staff_name = @staffName, updated_at = @at
            WHERE id = @id AND tenant_id = @tenantId AND mode = @from
            """, new { id, tenantId, from, to, staffKey, staffName, at = at.UtcDateTime }, cancellationToken: ct)) == 1;
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public Guid? ProjectId { get; init; }
        public string Mode { get; init; } = "";
        public string? StaffKey { get; init; }
        public string? StaffName { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public Conversation ToDomain() => new(Id, TenantId, CustomerId, ProjectId, Mode, StaffKey, StaffName,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }

    private sealed class MessageRow
    {
        public long Seq { get; init; }
        public Guid ConversationId { get; init; }
        public string Role { get; init; } = "";
        public string Text { get; init; } = "";
        public string? Author { get; init; }
        public DateTime At { get; init; }
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Lock _gate = new();
    private readonly List<Conversation> _conversations = [];
    private readonly List<(string TenantId, ConversationMessage Message)> _messages = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CreateAsync(Conversation conversation, CancellationToken ct)
    {
        lock (_gate) _conversations.Add(conversation);
        return Task.CompletedTask;
    }

    public Task<Conversation?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_conversations.FirstOrDefault(c => c.Id == id && c.TenantId == tenantId && (customerId is null || c.CustomerId == customerId)));
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(string tenantId, DateTimeOffset since, int limit, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Conversation>>([.. _conversations.Where(c => c.TenantId == tenantId && c.UpdatedAt >= since).OrderByDescending(c => c.UpdatedAt).Take(limit)]);
    }

    public Task<long> AppendAsync(string tenantId, Guid conversationId, string role, string text, string? author, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _conversations.FindIndex(c => c.Id == conversationId && c.TenantId == tenantId);
            if (i < 0) return Task.FromResult(0L);
            var seq = _messages.Count + 1L;
            _messages.Add((tenantId, new ConversationMessage(seq, conversationId, role, text, author, at)));
            _conversations[i] = _conversations[i] with { UpdatedAt = at };
            return Task.FromResult(seq);
        }
    }

    public Task<IReadOnlyList<ConversationMessage>> MessagesAsync(string tenantId, Guid conversationId, long after, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>([.. _messages
                .Where(m => m.TenantId == tenantId && m.Message.ConversationId == conversationId && m.Message.Seq > after).Select(m => m.Message).Take(limit)]);
        }
    }

    public Task<bool> SetModeAsync(string tenantId, Guid id, string from, string to, string? staffKey, string? staffName, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _conversations.FindIndex(c => c.Id == id && c.TenantId == tenantId && c.Mode == from);
            if (i < 0) return Task.FromResult(false);
            _conversations[i] = _conversations[i] with { Mode = to, StaffKey = staffKey, StaffName = staffName, UpdatedAt = at };
            return Task.FromResult(true);
        }
    }
}
