using BuilderPro.Identity.Api.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Identity.Api.Infrastructure;

/// <summary>
/// Postgres store: hand-written SQL you can read, Dapper only for parameters and row mapping.
/// The database ("identitydb") belongs to THIS service only: database-per-service. Adapted from
/// builderpro-ai-platform/platform/identity/PostgresMembershipStore.cs.
///
/// Schema changes are additive and versioned in identity.schema_history; never edit
/// an applied version, add a new one.
/// </summary>
public sealed class PostgresMembershipStore(NpgsqlDataSource db) : IMembershipStore
{
    // Aliases match MembershipRow's properties, so Dapper maps by name.
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, user_key AS UserKey, customer_id AS CustomerId, scopes AS Scopes,
        expires_at AS ExpiresAt, revoked_at AS RevokedAt, revoked_reason AS RevokedReason, created_at AS CreatedAt
        """;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS identity;
            CREATE TABLE IF NOT EXISTS identity.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS identity.memberships(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                user_key text NOT NULL,
                customer_id text NULL,
                scopes text[] NOT NULL DEFAULT '{}',
                expires_at timestamptz NULL,
                revoked_at timestamptz NULL,
                revoked_reason text NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE (tenant_id, user_key));
            CREATE INDEX IF NOT EXISTS ix_memberships_user ON identity.memberships(user_key);
            CREATE TABLE IF NOT EXISTS identity.membership_audit(
                id bigserial PRIMARY KEY,
                membership_id uuid NOT NULL,
                action text NOT NULL,
                reason text NULL,
                at timestamptz NOT NULL DEFAULT now());
            INSERT INTO identity.schema_history(version) VALUES (1) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public Task<IReadOnlyList<Membership>> FindByUserAsync(string userKey, CancellationToken ct) =>
        QueryAsync($"SELECT {Columns} FROM identity.memberships WHERE user_key = @userKey", new { userKey }, ct);

    public Task<IReadOnlyList<Membership>> ListByTenantAsync(string tenantId, CancellationToken ct) =>
        QueryAsync($"SELECT {Columns} FROM identity.memberships WHERE tenant_id = @tenantId ORDER BY user_key", new { tenantId }, ct);

    public async Task<Membership> UpsertAsync(string tenantId, string userKey, string? customerId, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        // One statement, one transaction: the row AND its audit entry, or neither.
        var rows = await QueryAsync($"""
            WITH upserted AS (
                INSERT INTO identity.memberships(id, tenant_id, user_key, customer_id, scopes, expires_at)
                VALUES (@id, @tenantId, @userKey, @customerId, @scopes, @expiresAt)
                ON CONFLICT (tenant_id, user_key) DO UPDATE
                    SET customer_id = EXCLUDED.customer_id, scopes = EXCLUDED.scopes, expires_at = EXCLUDED.expires_at,
                        revoked_at = NULL, revoked_reason = NULL, updated_at = now()
                RETURNING *),
            audit AS (
                INSERT INTO identity.membership_audit(membership_id, action) SELECT id, 'upsert' FROM upserted)
            SELECT {Columns} FROM upserted
            """,
            new { id = Guid.NewGuid(), tenantId, userKey, customerId, scopes = scopes.ToArray(), expiresAt = expiresAt?.UtcDateTime },
            ct);
        return rows[0];
    }

    public async Task<Membership?> RevokeAsync(Guid id, string reason, CancellationToken ct)
    {
        // COALESCE keeps the FIRST revocation time/reason: revoking twice changes nothing.
        var rows = await QueryAsync($"""
            WITH revoked AS (
                UPDATE identity.memberships
                   SET revoked_at = COALESCE(revoked_at, now()), revoked_reason = COALESCE(revoked_reason, @reason), updated_at = now()
                 WHERE id = @id
                RETURNING *),
            audit AS (
                INSERT INTO identity.membership_audit(membership_id, action, reason) SELECT id, 'revoke', @reason FROM revoked)
            SELECT {Columns} FROM revoked
            """, new { id, reason }, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)) == 1;
    }

    private async Task<IReadOnlyList<Membership>> QueryAsync(string sql, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MembershipRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// The row exactly as Postgres returns it. Npgsql gives timestamptz as UTC DateTime, so the
    /// conversion to the domain's DateTimeOffset happens here, once.
    /// </summary>
    private sealed class MembershipRow
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public string UserKey { get; init; } = "";
        public string? CustomerId { get; init; }
        public string[] Scopes { get; init; } = [];
        public DateTime? ExpiresAt { get; init; }
        public DateTime? RevokedAt { get; init; }
        public string? RevokedReason { get; init; }
        public DateTime CreatedAt { get; init; }

        public Membership ToDomain() => new(
            Id, TenantId, UserKey, CustomerId, Scopes,
            Utc(ExpiresAt), Utc(RevokedAt), RevokedReason, Utc(CreatedAt)!.Value);

        private static DateTimeOffset? Utc(DateTime? t) => t is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
    }
}
