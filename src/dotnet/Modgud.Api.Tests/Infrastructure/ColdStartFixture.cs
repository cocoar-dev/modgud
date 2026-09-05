using Cocoar.Configuration.Providers;
using Cocoar.Configuration.Testing;
using Testcontainers.PostgreSql;
using Modgud.Api;
using Modgud.Authentication.Identity;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container + cold-boot host for the cold-start test
/// collection. The inverse of <see cref="SharedPostgresFixture"/>: it boots the
/// app against a blank database and lets the genuine cold-boot path run
/// (CREATE DATABASE → schema → seed system realm + catalogs), WITHOUT pre-seeding
/// a default admin or resetting Marten data between tests.
///
/// <para>Two host flavors:</para>
/// <list type="bullet">
///   <item><description><see cref="Factory"/> — one host booted once against
///   <c>modgud_coldstart</c>. Stays pristine because the only mutating tests use
///   isolated hosts. Use it for read-only assertions about the cold-boot state
///   and for tenant-resolution tests.</description></item>
///   <item><description><see cref="CreateIsolatedHostAsync"/> — a fresh host
///   against a brand-new master DB (in the same container) for tests that mutate
///   realm/user state and want a throwaway "cold metal" boot.</description></item>
/// </list>
/// </summary>
public class ColdStartFixture : IAsyncLifetime
{
    private const string MasterDbName = "modgud_coldstart";

    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase(MasterDbName)
        // Cold-start tests provision multiple realm databases, each of which gets
        // its own Marten 9.x async-projection daemon holding Postgres connections.
        // The default server-global max_connections=100 is exhausted on contended
        // CI runners -> Npgsql "Timeout during connection attempt" (15s pool
        // timeout). Raise the ceiling to match SharedPostgresFixture.
        .WithCommand("-c", "max_connections=500")
        .Build();

    /// <summary>Config context for the shared <see cref="Factory"/> (master DB <c>modgud_coldstart</c>).</summary>
    public TestConfigurationContext TestContext { get; private set; } = null!;

    /// <summary>The shared cold-boot host — booted once, kept pristine.</summary>
    public ColdStartWebApplicationFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        TestContext = BuildContext(Container.GetConnectionString());
        CocoarTestConfiguration.Apply(TestContext);

        // Host is built lazily on first CreateClient() (the test base does that
        // under the same applied context).
        Factory = new ColdStartWebApplicationFactory();
    }

    /// <summary>
    /// Boots a fully-isolated cold-start host against a brand-new master database
    /// (a unique <c>coldstart_*</c> DB in the same container), so the test sees a
    /// genuinely pristine cold boot it can mutate and throw away. The caller owns
    /// the returned host and must dispose it.
    /// </summary>
    public async Task<IsolatedColdStartHost> CreateIsolatedHostAsync(
        bool useBackgroundProjectionDaemon = false)
    {
        var isolatedDb = "coldstart_" + Guid.NewGuid().ToString("N")[..12];
        var isolatedConnectionString = Container.GetConnectionString()
            .Replace($"Database={MasterDbName}", $"Database={isolatedDb}", StringComparison.OrdinalIgnoreCase);

        var ctx = BuildContext(isolatedConnectionString);
        CocoarTestConfiguration.Apply(ctx);

        ColdStartWebApplicationFactory factory = useBackgroundProjectionDaemon
            ? new SoloDaemonColdStartWebApplicationFactory()
            : new ColdStartWebApplicationFactory();
        // Force the host to build NOW, under this isolated context, so the
        // cold-boot bootstrap targets the isolated DB.
        factory.CreateClient().Dispose();

        // Restore the shared context for whatever the test does next.
        CocoarTestConfiguration.Apply(TestContext);

        return new IsolatedColdStartHost(factory);
    }

    /// <summary>
    /// Boots the production-shaped zero-realm state for first-installation
    /// tests. Unlike <see cref="CreateIsolatedHostAsync"/>, the test factory
    /// does not provision the legacy "system" test tenant.
    /// </summary>
    public async Task<UninitializedColdStartHost> CreateUninitializedHostAsync()
    {
        var isolatedDb = "install_" + Guid.NewGuid().ToString("N")[..12];
        var isolatedConnectionString = Container.GetConnectionString()
            .Replace($"Database={MasterDbName}", $"Database={isolatedDb}", StringComparison.OrdinalIgnoreCase);

        var ctx = BuildContext(isolatedConnectionString);
        CocoarTestConfiguration.Apply(ctx);

        var factory = new UninitializedModgudWebApplicationFactory();
        factory.CreateClient().Dispose();

        CocoarTestConfiguration.Apply(TestContext);
        return new UninitializedColdStartHost(factory);
    }

    private static TestConfigurationContext BuildContext(string connectionString) =>
        TestConfigurationContext.Replace(rule =>
        [
            rule.For<StartUpConfiguration>().FromStatic(_ => new StartUpConfiguration
            {
                AppUrl = "http://localhost:5000",
                CertPath = null, // Disable certificate loading
                DbSettings =
                {
                    ConnectionString = connectionString
                }
            }),
            rule.For<AppSettings>().FromStatic(_ => new AppSettings { AuthenticationMinimumLevel = 0 }),
            rule.For<EmailConfiguration>().FromStatic(_ => new EmailConfiguration()),
            rule.For<MagicLinkConfiguration>().FromStatic(_ => new MagicLinkConfiguration { Enabled = true }),
            rule.For<EmailOtpConfiguration>().FromStatic(_ => new EmailOtpConfiguration()),
            rule.For<OpenIddictSettings>().FromStatic(_ => new OpenIddictSettings
            {
                DevelopmentMode = true,
                AccessTokenLifetimeMinutes = 60,
                RefreshTokenLifetimeDays = 14,
                AuthorizationCodeLifetimeMinutes = 5,
            }),
            rule.For<ObservabilitySettings>().FromStatic(_ => new ObservabilitySettings
            {
                ServiceName = "modgud-coldstart-tests",
                Prometheus = new ObservabilitySettings.PrometheusSettings { Enabled = false },
                Otlp = new ObservabilitySettings.OtlpSettings { Enabled = false },
            }),
            // ADR 0010: no cross-node relay, no drain in the test host (single process).
            rule.For<ClusterSettings>().FromStatic(_ => new ClusterSettings { DrainDelaySeconds = 0 }),
        ]);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Container.DisposeAsync();
    }
}

/// <summary>
/// A throwaway, fully-isolated cold-start host (its own master DB). Dispose to
/// tear the host down; the DB is left in the shared container and reclaimed when
/// the container is disposed.
/// </summary>
public sealed class IsolatedColdStartHost(ColdStartWebApplicationFactory factory) : IAsyncDisposable
{
    public ColdStartWebApplicationFactory Factory { get; } = factory;

    public IServiceProvider Services => Factory.Services;

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class UninitializedColdStartHost(
    UninitializedModgudWebApplicationFactory factory) : IAsyncDisposable
{
    public UninitializedModgudWebApplicationFactory Factory { get; } = factory;
    public IServiceProvider Services => Factory.Services;
    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// Cold-start collection. Separate from the integration-test collection and, like
/// it, non-parallel — both serialize relative to each other, which keeps the
/// static <c>CocoarTestConfiguration</c> AsyncLocal from clashing across the two
/// fixtures.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ColdStartCollection : ICollectionFixture<ColdStartFixture>
{
    public const string Name = "Cold Start Tests";
}
