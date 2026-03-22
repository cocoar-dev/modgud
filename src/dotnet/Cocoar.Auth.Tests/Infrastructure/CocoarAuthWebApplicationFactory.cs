using Cocoar.Auth.Api.Configuration;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Infrastructure.Repositories;
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
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Configuration.Secrets;
using Cocoar.Configuration.Secrets.SecretTypes;
using Npgsql;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Each instance gets its own isolated set of databases from SharedPostgresFixture.
/// </summary>
public sealed class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>
{
	private readonly string _connectionString;
	private readonly string _password;

	/// <summary>
	/// Creates a factory with isolated databases.
	/// Call SharedPostgresFixture.CreateIsolatedDatabasesAsync() first to get the connection string.
	/// </summary>
	public CocoarAuthWebApplicationFactory(string isolatedConnectionString)
	{
		// Config connection string: password stripped (Program.cs adds it back via Secret)
		var npgsqlBuilder = new NpgsqlConnectionStringBuilder(isolatedConnectionString);
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
            new ShortGuidJsonConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter()
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
        CocoarTestConfiguration.ReplaceConfiguration(rule =>
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
            }),
            // Server settings (no SSL in tests)
            rule.For<ServerSettings>().FromStatic(_ => new ServerSettings())
        ]).ReplaceSecretsSetup(secrets => secrets.AllowPlaintext());

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Suppress verbose logging in tests (Marten schema SQL, Wolverine startup, etc.)
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
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

    /// <summary>
    /// Re-seeds the built-in login providers after database cleanup.
    /// Call this in tests that need the seeded "Internal" provider.
    /// </summary>
    public async Task SeedLoginProvidersAsync()
    {
        await Services.SeedLoginProvidersAsync();
    }

    /// <summary>
    /// Creates a realm and sets up an admin user in it in one call.
    /// Requires the caller to be logged in as system admin.
    /// </summary>
    public async Task CreateRealmWithAdminAsync(
        HttpClient adminClient, string slug, string adminUser, string adminPassword)
    {
        // Create the realm via system admin API
        var createDto = new CreateRealmDto { Slug = slug, DisplayName = slug };
        var createResponse = await adminClient.PostAsJsonAsync("/system/api/admin/realms", createDto, JsonOptions);
        if (createResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await createResponse.Content.ReadAsStringAsync();
            throw new Exception($"Failed to create realm '{slug}': {(int)createResponse.StatusCode} {body}");
        }

        // Create admin user via the realm's setup endpoint
        var setupDto = new { UserName = adminUser, Password = adminPassword, Email = $"{adminUser}@test.com" };
        var setupResponse = await adminClient.PostAsJsonAsync(
            $"/{slug}/api/setup/create-admin", setupDto, JsonOptions);
        if (!setupResponse.IsSuccessStatusCode)
        {
            var body = await setupResponse.Content.ReadAsStringAsync();
            throw new Exception($"Failed to create admin in realm '{slug}': {(int)setupResponse.StatusCode} {body}");
        }
    }
}
