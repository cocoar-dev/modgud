using Cocoar.Configuration.Providers;
using Cocoar.Configuration.Testing;
using Testcontainers.PostgreSql;
using Cocoar.Auth.Api;
using Cocoar.Auth.Authentication.Identity;

namespace Cocoar.Auth.Api.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container and WebApplicationFactory for all integration tests.
/// The host is created once and reused across all test classes in the collection.
/// Data is reset between tests via ResetMartenDataAsync().
/// </summary>
public class SharedPostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("timetodo_test")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    /// <summary>
    /// Test configuration context - created after container starts.
    /// Apply this in test class constructors to bridge the async context gap.
    /// </summary>
    public TestConfigurationContext TestContext { get; private set; } = null!;

    /// <summary>
    /// Shared factory — created once, reused across all tests.
    /// </summary>
    public CocoarAuthWebApplicationFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        // Create the test configuration context AFTER container is ready
        TestContext = TestConfigurationContext.Replace(rule =>
        [
            rule.For<StartUpConfiguration>().FromStatic(_ => new StartUpConfiguration
            {
                AppUrl = "http://localhost:5000",
                CertPath = null, // Disable certificate loading
                DbSettings =
                {
                    ConnectionString = ConnectionString
                }
            }),
            rule.For<AppSettings>().FromStatic(_ => new AppSettings { AuthenticationMinimumLevel = 0 }),
            // EmailConfiguration / MagicLinkConfiguration / EmailOtpConfiguration are
            // overridden at the DI level inside CocoarAuthWebApplicationFactory, but
            // Cocoar.Configuration still requires a rule per type so GetRequiredConfig
            // resolves before DI runs. Provide minimal defaults here.
            rule.For<EmailConfiguration>().FromStatic(_ => new EmailConfiguration()),
            rule.For<MagicLinkConfiguration>().FromStatic(_ => new MagicLinkConfiguration { Enabled = true }),
            rule.For<EmailOtpConfiguration>().FromStatic(_ => new EmailOtpConfiguration()),
            // OpenIddict in DevelopmentMode uses ephemeral signing keys — no cert file
            // needed, no real signing material required for tests.
            rule.For<OpenIddictSettings>().FromStatic(_ => new OpenIddictSettings
            {
                Issuer = "http://localhost:5000",
                DevelopmentMode = true,
                AccessTokenLifetimeMinutes = 60,
                RefreshTokenLifetimeDays = 14,
                AuthorizationCodeLifetimeMinutes = 5,
            }),
            // Observability: disable Prometheus + OTLP so the test host doesn't
            // need a bearer token or a collector. Activity buffer is still
            // wired (in-memory only), so metric-record sites stay exercised.
            rule.For<ObservabilitySettings>().FromStatic(_ => new ObservabilitySettings
            {
                ServiceName = "cocoar-auth-tests",
                Prometheus = new ObservabilitySettings.PrometheusSettings { Enabled = false },
                Otlp = new ObservabilitySettings.OtlpSettings { Enabled = false },
            }),
        ]);

        // Apply config before creating factory (must be in same async context)
        CocoarTestConfiguration.Apply(TestContext);

        // Create factory ONCE — the host (including async daemon) starts here
        Factory = new CocoarAuthWebApplicationFactory(this);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
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
