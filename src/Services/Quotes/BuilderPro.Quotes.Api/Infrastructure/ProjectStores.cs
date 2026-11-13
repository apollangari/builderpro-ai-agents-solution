using Dapper;
using Npgsql;

namespace BuilderPro.Quotes.Api.Infrastructure;

/// <summary>Projects in Postgres (quotes schema v4). Every read filters by tenant (and customer for customers).</summary>
public sealed class PostgresProjectStore(NpgsqlDataSource db) : IProjectStore
{
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, customer_id AS CustomerId, created_by AS CreatedBy, title AS Title, zip AS Zip,
        sku AS Sku, answers::text AS AnswersJson, stage AS Stage, quote_id AS QuoteId, created_at AS CreatedAt, updated_at AS UpdatedAt,
        salesperson_key AS SalespersonKey, salesperson_name AS SalespersonName
        """;

    private const string Visible = "tenant_id = @tenantId AND (@customerId::text IS NULL OR customer_id = @customerId)";

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS quotes;
            CREATE TABLE IF NOT EXISTS quotes.schema_history(
                version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());

            -- v4 (2n-f): projects (answers only; prices live in issued quotes).
            CREATE TABLE IF NOT EXISTS quotes.projects(
                id uuid PRIMARY KEY,
                tenant_id text NOT NULL,
                customer_id text NULL,
                created_by text NOT NULL,
                title text NOT NULL,
                zip text NOT NULL,
                sku text NULL,
                answers jsonb NOT NULL DEFAULT '{}'::jsonb,
                stage text NOT NULL,
                quote_id uuid NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_projects_tenant ON quotes.projects(tenant_id, customer_id, updated_at DESC);
            INSERT INTO quotes.schema_history(version) VALUES (4) ON CONFLICT DO NOTHING;

            -- v6 (1i): prepared by a salesperson on the customer's behalf (their sale, for commission).
            ALTER TABLE quotes.projects ADD COLUMN IF NOT EXISTS salesperson_key text NULL;
            ALTER TABLE quotes.projects ADD COLUMN IF NOT EXISTS salesperson_name text NULL;
            CREATE INDEX IF NOT EXISTS ix_projects_salesperson ON quotes.projects(tenant_id, salesperson_key) WHERE salesperson_key IS NOT NULL;
            INSERT INTO quotes.schema_history(version) VALUES (6) ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task CreateAsync(Project p, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quotes.projects(id, tenant_id, customer_id, created_by, title, zip, sku, answers, stage, quote_id, created_at, updated_at,
                                       salesperson_key, salesperson_name)
            VALUES (@Id, @TenantId, @CustomerId, @CreatedBy, @Title, @Zip, @Sku, @AnswersJson::jsonb, @Stage, @QuoteId, @CreatedAt, @UpdatedAt,
                    @SalespersonKey, @SalespersonName)
            """, Args(p), cancellationToken: ct));
    }

    public async Task UpdateAsync(Project p, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE quotes.projects SET title = @Title, zip = @Zip, sku = @Sku, answers = @AnswersJson::jsonb, stage = @Stage,
                   quote_id = @QuoteId, updated_at = @UpdatedAt
            WHERE id = @Id AND tenant_id = @TenantId
            """, Args(p), cancellationToken: ct));
    }

    public async Task<Project?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.projects WHERE {Visible} AND id = @id", new { tenantId, customerId, id }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Project>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM quotes.projects WHERE {Visible} ORDER BY updated_at DESC LIMIT @limit", new { tenantId, customerId, limit }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    private static object Args(Project p) => new
    {
        p.Id, p.TenantId, p.CustomerId, p.CreatedBy, p.Title, p.Zip, p.Sku, p.AnswersJson, p.Stage, p.QuoteId,
        p.SalespersonKey, p.SalespersonName,
        CreatedAt = p.CreatedAt.UtcDateTime, UpdatedAt = p.UpdatedAt.UtcDateTime,
    };

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = "";
        public string? CustomerId { get; init; }
        public string CreatedBy { get; init; } = "";
        public string Title { get; init; } = "";
        public string Zip { get; init; } = "";
        public string? Sku { get; init; }
        public string AnswersJson { get; init; } = "{}";
        public string Stage { get; init; } = "";
        public Guid? QuoteId { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? SalespersonKey { get; init; }
        public string? SalespersonName { get; init; }

        public Project ToDomain() => new(Id, TenantId, CustomerId, CreatedBy, Title, Zip, Sku, AnswersJson, Stage, QuoteId,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)),
            SalespersonKey, SalespersonName);
    }
}

/// <summary>For tests only.</summary>
public sealed class InMemoryProjectStore : IProjectStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Project> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CreateAsync(Project project, CancellationToken ct)
    {
        lock (_gate) _rows[project.Id] = project;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Project project, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(project.Id, out var old) && old.TenantId == project.TenantId) _rows[project.Id] = project;
        }
        return Task.CompletedTask;
    }

    public Task<Project?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue(id, out var p) && p.TenantId == tenantId && (customerId is null || p.CustomerId == customerId) ? p : null);
        }
    }

    public Task<IReadOnlyList<Project>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Project>>([.. _rows.Values
                .Where(p => p.TenantId == tenantId && (customerId is null || p.CustomerId == customerId))
                .OrderByDescending(p => p.UpdatedAt).Take(limit)]);
        }
    }
}
