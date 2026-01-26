using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Infrastructure.Services;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using JasperFx;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cocoar.Auth.Tests.Infrastructure;

public class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("cocoar_auth_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

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

    public void ClearCookies()
    {
        // Cookie clearing is handled via CleanDatabaseAsync
        // creating a fresh client with new options
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Remove existing Marten configuration and reconfigure with test container
            services.RemoveAll<IDocumentStore>();
            services.RemoveAll<IDocumentSession>();

            services.AddMarten(options =>
            {
                options.Connection(_postgresContainer.GetConnectionString());
                options.AutoCreateSchemaObjects = AutoCreate.All;

                // ═══════════════════════════════════════════════════════════════
                // DOCUMENT CONFIGURATION
                // ═══════════════════════════════════════════════════════════════

                options.Schema.For<ApplicationUser>()
                    .Identity(x => x.Id)
                    .Index(x => x.NormalizedUserName!, x => x.IsUnique = true)
                    .Index(x => x.NormalizedEmail!);

                options.Schema.For<ApplicationRole>()
                    .Identity(x => x.Id)
                    .Index(x => x.NormalizedName, x => x.IsUnique = true);

                options.Schema.For<UserSecurityData>()
                    .Identity(x => x.Id);

                // Configure UserSession document (ephemeral state, not event-sourced)
                options.Schema.For<UserSession>()
                    .Identity(x => x.Id)
                    .Index(x => x.UserId)
                    .Index(x => x.SessionId);

                // ═══════════════════════════════════════════════════════════════
                // EVENT SOURCING CONFIGURATION
                // ═══════════════════════════════════════════════════════════════

                // Register user events for the event store
                options.Events.AddEventType<UserCreated>();
                options.Events.AddEventType<UserNameChanged>();
                options.Events.AddEventType<UserEmailChanged>();
                options.Events.AddEventType<UserPhoneNumberChanged>();
                options.Events.AddEventType<UserProfileNameChanged>();
                options.Events.AddEventType<UserActivated>();
                options.Events.AddEventType<UserDeactivated>();
                options.Events.AddEventType<UserDeleted>();
                options.Events.AddEventType<UserRoleAssigned>();
                options.Events.AddEventType<UserRoleRemoved>();
                options.Events.AddEventType<UserClaimAdded>();
                options.Events.AddEventType<UserClaimRemoved>();
                options.Events.AddEventType<UserTwoFactorEnabled>();
                options.Events.AddEventType<UserTwoFactorDisabled>();
                options.Events.AddEventType<UserRecoveryCodesRegenerated>();
                options.Events.AddEventType<UserSessionsInvalidated>();
                options.Events.AddEventType<UserLoggedIn>();
                options.Events.AddEventType<UserLoginFailed>();
                options.Events.AddEventType<UserLockedOut>();
                options.Events.AddEventType<UserUnlocked>();
                options.Events.AddEventType<UserEmailConfirmed>();
                options.Events.AddEventType<UserPhoneNumberConfirmed>();

                // Register GDPR events for the event store
                options.Events.AddEventType<UserDeletionRequested>();
                options.Events.AddEventType<UserDeletionCancelled>();
                options.Events.AddEventType<UserDataMasked>();
                options.Events.AddEventType<UserDataExported>();
                options.Events.AddEventType<UserRestored>();

                // Register role events for the event store
                options.Events.AddEventType<RoleCreated>();
                options.Events.AddEventType<RoleNameChanged>();
                options.Events.AddEventType<RoleDescriptionChanged>();
                options.Events.AddEventType<RoleDeleted>();
                options.Events.AddEventType<RoleClaimAdded>();
                options.Events.AddEventType<RoleClaimRemoved>();

                // ═══════════════════════════════════════════════════════════════
                // STATE PROJECTIONS (Inline for tests - immediate consistency)
                // ═══════════════════════════════════════════════════════════════

                options.Projections.Add(new UserStateProjection(), ProjectionLifecycle.Inline);
                options.Projections.Add(new RoleStateProjection(), ProjectionLifecycle.Inline);
                // Use inline projection in tests for immediate consistency
                options.Projections.Add(new UserDetailsProjection(), ProjectionLifecycle.Inline);

                // Configure UserState indexes for fast lookups
                options.Schema.For<UserState>()
                    .Identity(x => x.Id)
                    .Index(x => x.NormalizedUserName, x => x.IsUnique = true)
                    .Index(x => x.NormalizedEmail);

                // Configure RoleState indexes for fast lookups
                options.Schema.For<RoleState>()
                    .Identity(x => x.Id)
                    .Index(x => x.NormalizedName, x => x.IsUnique = true);
            })
            .UseLightweightSessions();
        });

        builder.UseEnvironment("Testing");
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        // Dispose WebApplicationFactory first (stops Wolverine/host) before disposing postgres
        try
        {
            await base.DisposeAsync();
        }
        catch (AggregateException)
        {
            // Suppress Wolverine shutdown exceptions during test cleanup
        }

        await _postgresContainer.DisposeAsync();
    }

    public async Task<ApplicationUser> CreateTestUserAsync(
        string userName = "testuser",
        string password = "Test123!@#",
        string? email = null,
        bool isAdmin = false)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var user = new ApplicationUser(userName, email ?? $"{userName}@test.com");
        user.SetFirstName("Test");
        user.SetLastName("User");

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
        ClearCookies();

        // Clear mock email sender
        var emailSender = GetMockEmailSender();
        emailSender.Clear();

        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Clear all user-related documents (these are safe - Marten handles missing tables)
        session.DeleteWhere<ApplicationUser>(u => true);
        session.DeleteWhere<ApplicationRole>(r => true);
        session.DeleteWhere<UserSecurityData>(u => true);
        session.DeleteWhere<UserState>(u => true);
        session.DeleteWhere<RoleState>(r => true);
        session.DeleteWhere<UserSession>(s => true);
        session.DeleteWhere<Cocoar.Auth.Application.Models.UserDetailsReadModel>(u => true);
        await session.SaveChangesAsync();

        // Clear event streams - only if tables exist (first test run may not have them yet)
        await using var conn = new NpgsqlConnection(_postgresContainer.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE public.mt_events, public.mt_streams CASCADE;", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // relation does not exist
        {
            // Tables don't exist yet, that's fine - nothing to clean
        }
    }
}
