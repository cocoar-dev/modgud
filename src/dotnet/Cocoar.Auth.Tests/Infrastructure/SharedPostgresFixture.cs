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

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        // Note: CocoarTestConfiguration.ReplaceAllRules is called in WebApplicationFactory
        // because AsyncLocal requires it to be in the same async context as host creation
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
