using Cocoar.Configuration.Providers;
using Cocoar.Configuration.Testing;
using Npgsql;
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
///
/// <para>With <c>MODGUD_TEST_POSTGRES_CONNECTION_STRING</c> set (the same opt-in as
/// <see cref="SharedPostgresFixture"/>), no container starts: every host boots against a
/// new database on the dedicated postgres-dev (127.0.0.1:5433), each verified not to exist
/// yet, and the host creates it itself as in a real cold boot. The names are derived from
/// the supplied <c>modgud_review_*</c> name but never start with <c>&lt;name&gt;_</c>, so a
/// run that also uses the shared fixture keeps its own freshness check intact. The
/// databases stay on postgres-dev for diagnosis; nothing is dropped.</para>
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

    /// <summary>The validated postgres-dev target in development mode; null with Testcontainers.</summary>
    private string? _development;

    public async ValueTask InitializeAsync()
    {
        var development = Environment.GetEnvironmentVariable("MODGUD_TEST_POSTGRES_CONNECTION_STRING");
        string master;
        if (development is null)
        {
            await Container.StartAsync();
            master = Container.GetConnectionString();
        }
        else
        {
            _development = TestPostgres.ValidateDevelopmentConnectionString(development);
            master = await DevelopmentTargetAsync("cs", unique: false);
        }

        TestContext = BuildContext(master);
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
        var isolatedConnectionString = _development is not null
            ? await DevelopmentTargetAsync("ci", unique: true)
            : Container.GetConnectionString().Replace($"Database={MasterDbName}",
                "Database=coldstart_" + Guid.NewGuid().ToString("N")[..12], StringComparison.OrdinalIgnoreCase);

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
        var isolatedConnectionString = _development is not null
            ? await DevelopmentTargetAsync("in", unique: true)
            : Container.GetConnectionString().Replace($"Database={MasterDbName}",
                "Database=install_" + Guid.NewGuid().ToString("N")[..12], StringComparison.OrdinalIgnoreCase);

        var ctx = BuildContext(isolatedConnectionString);
        CocoarTestConfiguration.Apply(ctx);

        var factory = new UninitializedModgudWebApplicationFactory();
        factory.CreateClient().Dispose();

        CocoarTestConfiguration.Apply(TestContext);
        return new UninitializedColdStartHost(factory);
    }

    /// <summary>A new postgres-dev database name derived from the supplied one:
    /// <c>modgud_review_&lt;kind&gt;&lt;suffix&gt;[&lt;random&gt;]</c>, verified not to exist yet.</summary>
    private async Task<string> DevelopmentTargetAsync(string kind, bool unique)
    {
        var builder = new NpgsqlConnectionStringBuilder(_development);
        var suffix = builder.Database!["modgud_review_".Length..];
        builder.Database = $"modgud_review_{kind}{suffix}" + (unique ? Guid.NewGuid().ToString("N")[..8] : "");
        return await TestPostgres.EnsureFreshDevelopmentDatabaseAsync(builder.ConnectionString);
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
                    ConnectionString = TestPostgres.WithGenerousConnectTimeout(connectionString)
                }
            }),
            rule.For<AppSettings>().FromStatic(_ => new AppSettings()),
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
            // ADR 0022: no cross-node relay, no drain in the test host (single process).
            rule.For<ClusterSettings>().FromStatic(_ => new ClusterSettings { DrainDelaySeconds = 0 }),
            rule.For<Modgud.Infrastructure.Http.OutboundHttpSettings>().FromStatic(_ => new Modgud.Infrastructure.Http.OutboundHttpSettings()),
        ]);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        // postgres-dev and its test databases stay for diagnosis; only a container we
        // started is ours to remove.
        if (_development is null)
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
