using Npgsql;
using Testcontainers.PostgreSql;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container for all integration tests.
/// Starts once, reused across all test classes in the collection.
/// </summary>
public class SharedPostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("cocoar_auth_test")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    /// <summary>
    /// Base DB name from container config. Program.cs derives _master and _system from this.
    /// </summary>
    private string BaseDbName
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            return builder.Database ?? "cocoar_auth_test";
        }
    }

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        // Pre-create master + system databases (same names Program.cs will derive)
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        foreach (var suffix in new[] { "_master", "_system" })
        {
            var dbName = BaseDbName + suffix;
            await using var checkCmd = new NpgsqlCommand(
                $"SELECT 1 FROM pg_database WHERE datname = '{dbName}'", conn);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists is null)
            {
                await using var createCmd = new NpgsqlCommand(
                    $"CREATE DATABASE \"{dbName}\"", conn);
                await createCmd.ExecuteNonQueryAsync();
            }
        }
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}

/// <summary>
/// Collection definition - all test classes with [Collection(Name)] share the same fixtures.
/// DisableParallelization ensures tests run sequentially to avoid conflicts with shared database.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class IntegrationTestCollection : ICollectionFixture<SharedPostgresFixture>
{
    public const string Name = "Integration Tests";
}
