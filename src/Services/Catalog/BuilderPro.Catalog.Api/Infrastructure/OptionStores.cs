using System.Text.Json;
using BuilderPro.Catalog.Api.Domain;
using Dapper;
using Npgsql;

namespace BuilderPro.Catalog.Api.Infrastructure;

/// <summary>
/// Option steps in Postgres (schema v4, additive). A group is one row; its options travel as jsonb
/// (they are always read and written together). Every query filters by tenant_id.
/// </summary>
public sealed class PostgresOptionStore(NpgsqlDataSource db) : IOptionStore
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS catalog;
            CREATE TABLE IF NOT EXISTS catalog.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v4 (2n-c/d): option steps and category minimums.
            CREATE TABLE IF NOT EXISTS catalog.option_groups(
                tenant_id text NOT NULL,
                group_id text NOT NULL,
                category text NOT NULL,
                sort integer NOT NULL DEFAULT 0,
                definition jsonb NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (tenant_id, group_id));
            CREATE INDEX IF NOT EXISTS ix_option_groups_category ON catalog.option_groups(tenant_id, category, sort);
            CREATE TABLE IF NOT EXISTS catalog.category_rules(
                tenant_id text NOT NULL,
                category text NOT NULL,
                minimum_quantity numeric(12,3) NOT NULL CHECK (minimum_quantity >= 0),
                PRIMARY KEY (tenant_id, category));
            INSERT INTO catalog.schema_history(version) VALUES (4) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<OptionGroup>> ListGroupsAsync(string tenantId, string category, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT definition::text FROM catalog.option_groups WHERE tenant_id = @tenantId AND category = @category ORDER BY sort, group_id",
            new { tenantId, category }, cancellationToken: ct));
        return [.. rows.Select(json => JsonSerializer.Deserialize<OptionGroup>(json)!)];
    }

    public async Task<decimal?> MinimumAsync(string tenantId, string category, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<decimal?>(new CommandDefinition(
            "SELECT minimum_quantity FROM catalog.category_rules WHERE tenant_id = @tenantId AND category = @category",
            new { tenantId, category }, cancellationToken: ct));
    }

    public async Task UpsertGroupAsync(OptionGroup g, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO catalog.option_groups(tenant_id, group_id, category, sort, definition)
            VALUES (@TenantId, @GroupId, @Category, @Sort, @Json::jsonb)
            ON CONFLICT (tenant_id, group_id) DO UPDATE
                SET category = EXCLUDED.category, sort = EXCLUDED.sort, definition = EXCLUDED.definition, updated_at = now()
            """, new { g.TenantId, g.GroupId, g.Category, g.Sort, Json = JsonSerializer.Serialize(g) }, cancellationToken: ct));
    }

    public async Task UpsertRuleAsync(CategoryRule r, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO catalog.category_rules(tenant_id, category, minimum_quantity) VALUES (@TenantId, @Category, @MinimumQuantity)
            ON CONFLICT (tenant_id, category) DO UPDATE SET minimum_quantity = EXCLUDED.minimum_quantity
            """, r, cancellationToken: ct));
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryOptionStore : IOptionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string Group), OptionGroup> _groups = [];
    private readonly Dictionary<(string Tenant, string Category), decimal> _rules = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<OptionGroup>> ListGroupsAsync(string tenantId, string category, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OptionGroup>>([.. _groups.Values
                .Where(g => g.TenantId == tenantId && g.Category == category)
                .OrderBy(g => g.Sort).ThenBy(g => g.GroupId, StringComparer.Ordinal)]);
        }
    }

    public Task<decimal?> MinimumAsync(string tenantId, string category, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rules.TryGetValue((tenantId, category), out var m) ? m : (decimal?)null);
    }

    public Task UpsertGroupAsync(OptionGroup group, CancellationToken ct)
    {
        lock (_gate) _groups[(group.TenantId, group.GroupId)] = group;
        return Task.CompletedTask;
    }

    public Task UpsertRuleAsync(CategoryRule rule, CancellationToken ct)
    {
        lock (_gate) _rules[(rule.TenantId, rule.Category)] = rule.MinimumQuantity;
        return Task.CompletedTask;
    }
}
