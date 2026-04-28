using Npgsql;
using Testcontainers.PostgreSql;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container for all integration tests.
/// The container is a static singleton — started once, reused across ALL collections.
/// Each test class gets its own isolated database via CreateIsolatedDatabasesAsync().
/// Multiple collections run in PARALLEL while sharing the same container.
/// </summary>
public class SharedPostgresFixture : IAsyncLifetime
{
    // Static container — ONE instance shared across all collection fixture instances.
    private static readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("postgres")
        .WithCommand("-c", "max_connections=500")
        .Build();

    private static readonly SemaphoreSlim _startLock = new(1, 1);
    private static bool _started;
    private static int _dbCounter;

    public async Task InitializeAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (!_started)
            {
                await _container.StartAsync();
                _started = true;
            }
        }
        finally { _startLock.Release(); }
    }

    /// <summary>
    /// Creates an isolated database for a test class.
    /// Thread-safe — can be called concurrently from parallel collections.
    /// </summary>
    public async Task<string> CreateIsolatedDatabasesAsync()
    {
        var id = Interlocked.Increment(ref _dbCounter);
        var dbName = $"test_{id}";

        var connectionString = _container.GetConnectionString();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = dbName
        };
        return builder.ConnectionString;
    }

    // Container lives for the entire process — Testcontainers Ryuk handles cleanup.
    public Task DisposeAsync() => Task.CompletedTask;
}

// ═══════════════════════════════════════════════════════════════════════════
// COLLECTION DEFINITIONS — each collection runs in PARALLEL with the others.
// Tests WITHIN a collection run sequentially (xUnit default).
// All collections share the same static PostgreSQL container.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Admin CRUD: users, roles, OAuth clients/scopes/APIs, login providers, groups, realms</summary>
[CollectionDefinition(Name)]
public class AdminCollection : ICollectionFixture<SharedPostgresFixture>
{
    public const string Name = "Admin";
}

/// <summary>Auth flows: login, registration, password, email, profile, lockout, sessions, GDPR</summary>
[CollectionDefinition(Name)]
public class AuthCollection : ICollectionFixture<SharedPostgresFixture>
{
    public const string Name = "Auth";
}

/// <summary>OAuth & Security: OAuth flows, consent, tokens, 2FA, WebAuthn, external login</summary>
[CollectionDefinition(Name)]
public class OAuthSecurityCollection : ICollectionFixture<SharedPostgresFixture>
{
    public const string Name = "OAuth & Security";
}

/// <summary>Platform: projections, multi-tenancy, setup, OWASP security</summary>
[CollectionDefinition(Name)]
public class PlatformCollection : ICollectionFixture<SharedPostgresFixture>
{
    public const string Name = "Platform";
}
