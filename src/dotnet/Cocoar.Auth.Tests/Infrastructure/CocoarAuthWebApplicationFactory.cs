using Cocoar.Auth.Api.Configuration;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Infrastructure.Services;
using Cocoar.Configuration.Providers;
using Cocoar.Configuration.Testing;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Cocoar.Configuration.Secrets;
using Cocoar.Configuration.Secrets.SecretTypes;
using Npgsql;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Requires SharedPostgresFixture to be initialized first (sets up Cocoar.Configuration).
/// </summary>
public sealed class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>
{
	
	private readonly string _connectionString;
	private readonly string _password;

	public CocoarAuthWebApplicationFactory(SharedPostgresFixture fixture)
    {
        //_connectionString = fixture.ConnectionString;
		var npgsqlBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
		_password = npgsqlBuilder.Password ?? "";
		npgsqlBuilder.Password = null;
		_connectionString = npgsqlBuilder.ConnectionString;
	}

    public JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new OptionalJsonConverterFactory(),
            new ShortGuidJsonConverter()
        },
        TypeInfoResolver = new OptionalAwareTypeInfoResolver()
    };

    public HttpClient CreateClientWithCookies()
    {
        var options = new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        };

        return CreateClient(options);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // IMPORTANT: Must set test configuration in the SAME async context as host creation
        // because CocoarTestConfiguration uses AsyncLocal<T>
        CocoarTestConfiguration.ReplaceAllRules(rule =>
        [
            rule.For<DatabaseSettings>().FromStatic(_ => new DatabaseSettings
            {
                ConnectionString = _connectionString,
				Password = Secret.FromPlain(_password)
            }),
            rule.For<AuthSettings>().FromStatic(_ => new AuthSettings
            {
                Cookie = new CookieSettings
                {
                    SecurePolicy = "None" // Allow cookies over HTTP for testing
                }
            }),
            rule.For<CorsSettings>().FromStatic(_ => new CorsSettings
            {
                AllowedOrigins = ["http://localhost"],
                AllowCredentials = true
            }),
            // Use inline projections in tests to avoid async daemon locking issues
            rule.For<ProjectionSettings>().FromStatic(_ => new ProjectionSettings
            {
                UseAsyncProjections = false
            }),
            // SMTP settings (not used in tests, but required for configuration)
            rule.For<SmtpSettings>().FromStatic(_ => new SmtpSettings
            {
                Host = "localhost",
                Port = 25,
                UseSsl = false,
                FromAddress = "test@localhost",
                FromName = "Test"
            }),
            // WebAuthn settings
            rule.For<WebAuthnSettings>().FromStatic(_ => new WebAuthnSettings
            {
                RelyingPartyId = "localhost",
                RelyingPartyName = "Cocoar Auth Test",
                Origins = ["http://localhost"]
            }),
            // OpenIddict settings
            rule.For<OpenIddictSettings>().FromStatic(_ => new OpenIddictSettings
            {
                Issuer = "http://localhost",
                DevelopmentMode = true,
                AccessTokenLifetimeMinutes = 60,
                RefreshTokenLifetimeDays = 14,
                AuthorizationCodeLifetimeMinutes = 5
            })
        ],
	        setup => [
				setup.Secrets().AllowPlaintext()
				]);

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Clear logging providers to avoid Event Log dispose issues on Windows
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
        });

        builder.ConfigureServices(services =>
        {
            // Remove EventLog provider explicitly (Windows adds it by default and it causes dispose issues)
            services.RemoveAll<ILoggerProvider>();

            // Configure cookie to work over HTTP (no secure flag) for testing
            services.PostConfigure<CookieAuthenticationOptions>(
                IdentityConstants.ApplicationScheme,
                options =>
                {
                    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                });

            // Disable HTTPS requirement for OpenIddict in tests
            services.PostConfigure<OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreOptions>(
                options =>
                {
                    options.DisableTransportSecurityRequirement = true;
                });
        });
    }

    public async Task<ApplicationUser> CreateTestUserAsync(
        string userName = "testuser",
        string password = "Test123!@#",
        string? email = null,
        bool isAdmin = false,
        bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var user = new ApplicationUser(userName, email ?? $"{userName}@test.com");
        user.SetFirstName("Test");
        user.SetLastName("User");
        user.SetIsActive(isActive);

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new Exception($"Failed to create test user: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        if (isAdmin)
        {
            var adminRole = await roleManager.FindByNameAsync("Admin");
            if (adminRole is null)
            {
                adminRole = new ApplicationRole("Admin", "Administrator role");
                await roleManager.CreateAsync(adminRole);
            }
            await userManager.AddToRoleAsync(user, "Admin");
        }

        return user;
    }

    public async Task<ApplicationRole> CreateTestRoleAsync(string name = "TestRole", string? description = null)
    {
        using var scope = Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var role = new ApplicationRole(name, description);
        var result = await roleManager.CreateAsync(role);

        if (!result.Succeeded)
        {
            throw new Exception($"Failed to create test role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        return role;
    }

    public MockEmailSender GetMockEmailSender()
    {
        return Services.GetRequiredService<MockEmailSender>();
    }

    public async Task SeedOpenIddictScopesAsync()
    {
        await Cocoar.Auth.Infrastructure.OpenIddict.OpenIddictExtensions.SeedOpenIddictScopesAsync(Services);
    }

    public async Task CleanDatabaseAsync()
    {
        // Clear mock email sender
        var emailSender = GetMockEmailSender();
        emailSender.Clear();

        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Clear all user-related documents
        session.DeleteWhere<ApplicationUser>(u => true);
        session.DeleteWhere<ApplicationRole>(r => true);
        session.DeleteWhere<UserSecurityData>(u => true);
        session.DeleteWhere<UserState>(u => true);
        session.DeleteWhere<RoleState>(r => true);
        session.DeleteWhere<UserSession>(s => true);
        session.DeleteWhere<Cocoar.Auth.Application.Models.UserDetailsReadModel>(u => true);

        // Clear OAuth-related documents
        session.DeleteWhere<OAuthApplicationState>(o => true);
        session.DeleteWhere<OAuthApplicationSecurityData>(o => true);
        session.DeleteWhere<OAuthScopeState>(o => true);
        session.DeleteWhere<OAuthApiResourceState>(o => true);
        session.DeleteWhere<OAuthApiResourceSecurityData>(o => true);
        session.DeleteWhere<OpenIddictAuthorizationDocument>(o => true);
        session.DeleteWhere<OpenIddictTokenDocument>(o => true);
        await session.SaveChangesAsync();

		// Clear event streams
		var dataSource = Services.GetRequiredService<NpgsqlDataSource>();

		await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE public.mt_events, public.mt_streams CASCADE;", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Tables don't exist yet
        }
    }
}
