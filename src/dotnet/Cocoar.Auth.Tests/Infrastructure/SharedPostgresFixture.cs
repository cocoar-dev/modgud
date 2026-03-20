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
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
    }

    /// <summary>
    /// Creates an isolated set of databases (base + _master + _system) for a test class.
    /// Returns a connection string pointing at the base DB name.
    /// Program.cs will derive _master and _system from this base name.
    /// </summary>
    public async Task<string> CreateIsolatedDatabasesAsync()
    {
        var id = Interlocked.Increment(ref _dbCounter);
        var baseName = $"test_{id}";

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var suffix in new[] { "", "_master", "_system" })
        {
            var dbName = baseName + suffix;
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
            _createdDatabases.Add(dbName);
        }

        // Return connection string with the base DB name
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = baseName
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
