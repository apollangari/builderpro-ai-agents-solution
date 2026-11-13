using BuilderPro.Identity.Api.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Identity.Api.Infrastructure;

/// <summary>Append-only consent history in identitydb (schema version 2). No UPDATE or DELETE exists here.</summary>
public sealed class PostgresConsentStore(NpgsqlDataSource db) : IConsentStore
{
    private const string Columns = """
        id AS Id, user_key AS UserKey, consent AS Consent, hours AS Hours,
        wording_version AS WordingVersion, source AS Source, at AS At
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS identity;
            CREATE TABLE IF NOT EXISTS identity.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS identity.consent_events(
                id bigserial PRIMARY KEY,
                user_key text NOT NULL,
                consent text NOT NULL CHECK (consent IN ('call','text','both','none')),
                hours text NOT NULL CHECK (hours IN ('business','evenings','weekends','anytime')),
                wording_version text NOT NULL,
                source text NOT NULL,
                at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_consent_user ON identity.consent_events(user_key, id DESC);
            INSERT INTO identity.schema_history(version) VALUES (2) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task<ConsentEvent?> LatestAsync(string userKey, CancellationToken ct) =>
        (await HistoryAsync(userKey, 1, ct)).FirstOrDefault();

    public async Task<ConsentEvent> AppendAsync(string userKey, string consent, string hours, string wordingVersion, string source, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition($"""
            INSERT INTO identity.consent_events(user_key, consent, hours, wording_version, source, at)
            VALUES (@userKey, @consent, @hours, @wordingVersion, @source, @at)
            RETURNING {Columns}
            """, new { userKey, consent, hours, wordingVersion, source, at = at.UtcDateTime }, cancellationToken: ct));
        return row.ToDomain();
    }

    public async Task<IReadOnlyList<ConsentEvent>> HistoryAsync(string userKey, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM identity.consent_events WHERE user_key = @userKey ORDER BY id DESC LIMIT @limit",
            new { userKey, limit }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public string UserKey { get; init; } = "";
        public string Consent { get; init; } = "";
        public string Hours { get; init; } = "";
        public string WordingVersion { get; init; } = "";
        public string Source { get; init; } = "";
        public DateTime At { get; init; }

        public ConsentEvent ToDomain() =>
            new(Id, UserKey, Consent, Hours, WordingVersion, Source, new DateTimeOffset(DateTime.SpecifyKind(At, DateTimeKind.Utc)));
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryConsentStore : IConsentStore
{
    private readonly Lock _gate = new();
    private readonly List<ConsentEvent> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<ConsentEvent?> LatestAsync(string userKey, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rows.LastOrDefault(e => e.UserKey == userKey));
    }

    public Task<ConsentEvent> AppendAsync(string userKey, string consent, string hours, string wordingVersion, string source, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var e = new ConsentEvent(_rows.Count + 1, userKey, consent, hours, wordingVersion, source, at);
            _rows.Add(e);
            return Task.FromResult(e);
        }
    }

    public Task<IReadOnlyList<ConsentEvent>> HistoryAsync(string userKey, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ConsentEvent>>([.. _rows.Where(e => e.UserKey == userKey).Reverse().Take(limit)]);
        }
    }
}
