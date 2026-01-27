using Cocoar.Auth.Api.Configuration;
using Cocoar.Auth.Domain.Entities;
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
using Npgsql;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Requires SharedPostgresFixture to be initialized first (sets up Cocoar.Configuration).
/// </summary>
public sealed class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public CocoarAuthWebApplicationFactory(SharedPostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
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
                ConnectionString = _connectionString
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
            })
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
        await session.SaveChangesAsync();

        // Clear event streams
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
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
