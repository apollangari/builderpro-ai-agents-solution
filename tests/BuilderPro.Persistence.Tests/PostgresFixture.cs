using Npgsql;
using Testcontainers.PostgreSql;

namespace BuilderPro.Persistence.Tests;

/// <summary>
/// One Postgres container for the whole test run; every test gets its OWN fresh database,
/// so tests never see each other's rows (same idea as database-per-service).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<NpgsqlDataSource> NewDatabaseAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(_container.GetConnectionString()))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        return NpgsqlDataSource.Create(csb.ConnectionString);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
