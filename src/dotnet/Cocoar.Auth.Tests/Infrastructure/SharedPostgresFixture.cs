using System.Collections.Concurrent;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container for all integration tests.
/// Starts once, reused across all test classes in the collection.
/// Each test class gets its own isolated set of databases via CreateIsolatedDatabases().
/// </summary>
public class SharedPostgresFixture : IAsyncLifetime
{
    private int _dbCounter;
    private readonly ConcurrentBag<string> _createdDatabases = [];

    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("postgres") // Connect to default DB; test DBs created on demand
        .WithCommand("-c", "max_connections=500") // Wolverine + parallel tests need more connections
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
    }

    /// <summary>
    /// Creates an isolated database for a test class.
    /// Single DB serves as both tenant registry and system tenant (like alert-hub pattern).
    /// Returns a connection string pointing at the isolated DB.
    /// </summary>
    public async Task<string> CreateIsolatedDatabasesAsync()
    {
        var id = Interlocked.Increment(ref _dbCounter);
        var dbName = $"test_{id}";

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        _createdDatabases.Add(dbName);

        // Return connection string pointing at the isolated DB
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = dbName
        };
        return builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}

/// <summary>
/// Collection definition — all test classes share ONE PostgreSQL container
/// but each gets its own isolated databases, so parallelization is safe.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<SharedPostgresFixture>
{
    public const string Name = "Integration Tests";
}
