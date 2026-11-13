using BuilderPro.Identity.Api.Domain;
using Npgsql;

namespace BuilderPro.Identity.Api.Infrastructure;

/// <summary>
/// Postgres store with plain SQL (no ORM): you can read every query. The database
/// ("identitydb") belongs to THIS service only: database-per-service. Adapted from
/// builderpro-ai-platform/platform/identity/PostgresMembershipStore.cs.
///
/// Schema changes are additive and versioned in identity.schema_history; never edit
/// an applied version, add a new one.
/// </summary>
public sealed class PostgresMembershipStore(NpgsqlDataSource db) : IMembershipStore
{
    private const string Columns = "id, tenant_id, user_key, customer_id, scopes, expires_at, revoked_at, revoked_reason, created_at";

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
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
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Membership>> FindByUserAsync(string userKey, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM identity.memberships WHERE user_key = $1");
        cmd.Parameters.AddWithValue(userKey);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<Membership>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM identity.memberships WHERE tenant_id = $1 ORDER BY user_key");
        cmd.Parameters.AddWithValue(tenantId);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<Membership> UpsertAsync(string tenantId, string userKey, string? customerId, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        // One statement, one transaction: the row AND its audit entry, or neither.
        await using var cmd = db.CreateCommand($"""
            WITH upserted AS (
                INSERT INTO identity.memberships(id, tenant_id, user_key, customer_id, scopes, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT (tenant_id, user_key) DO UPDATE
                    SET customer_id = EXCLUDED.customer_id, scopes = EXCLUDED.scopes, expires_at = EXCLUDED.expires_at,
                        revoked_at = NULL, revoked_reason = NULL, updated_at = now()
                RETURNING {Columns}),
            audit AS (
                INSERT INTO identity.membership_audit(membership_id, action) SELECT id, 'upsert' FROM upserted)
            SELECT {Columns} FROM upserted
            """);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(userKey);
        cmd.Parameters.AddWithValue((object?)customerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(scopes.ToArray());
        cmd.Parameters.AddWithValue((object?)expiresAt ?? DBNull.Value);
        return (await ReadAllAsync(cmd, ct))[0];
    }

    public async Task<Membership?> RevokeAsync(Guid id, string reason, CancellationToken ct)
    {
        // COALESCE keeps the FIRST revocation time/reason: revoking twice changes nothing.
        await using var cmd = db.CreateCommand($"""
            WITH revoked AS (
                UPDATE identity.memberships
                   SET revoked_at = COALESCE(revoked_at, now()), revoked_reason = COALESCE(revoked_reason, $2), updated_at = now()
                 WHERE id = $1
                RETURNING {Columns}),
            audit AS (
                INSERT INTO identity.membership_audit(membership_id, action, reason) SELECT id, 'revoke', $2 FROM revoked)
            SELECT {Columns} FROM revoked
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(reason);
        var rows = await ReadAllAsync(cmd, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        return await cmd.ExecuteScalarAsync(ct) is 1;
    }

    private static async Task<IReadOnlyList<Membership>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<Membership>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Membership(
                r.GetGuid(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<string[]>(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.GetFieldValue<DateTimeOffset>(8)));
        }
        return list;
    }
}
