using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Domain.Users.Events;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;
using Cocoar.Auth.Api;
using Cocoar.Auth.Authentication.Api.Account;
using Cocoar.Auth.Infrastructure.Email;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authorization.Access;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;

namespace Cocoar.Auth.Api.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Created once in SharedPostgresFixture and reused across all tests.
/// Test configuration is applied via CocoarTestConfiguration.Apply() before creation.
/// </summary>
public sealed class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    private IHost? _host;

    public CocoarAuthWebApplicationFactory(SharedPostgresFixture fixture)
    {
        // No configuration needed here - CocoarTestConfiguration.Apply()
        // was already called in the fixture
    }

    public JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = null, // Match API's behavior (no camelCase)
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new OptionalAwareTypeInfoResolver(),
        Converters =
        {
            new JsonStringEnumConverter(),
            new OptionalJsonConverterFactory()
        }
    };

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

            // Override cookie security for tests (TestServer uses HTTP, not HTTPS)
            services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });
            services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                IdentityConstants.TwoFactorUserIdScheme, options =>
            {
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });

            // Replace email service with in-memory implementation for tests
            // Must construct manually to avoid circular dependency:
            // InMemoryEmailService(ILogger, IEmailService?) → IEmailService = InMemoryEmailService → loop
            services.RemoveAll<IEmailService>();
            services.AddSingleton(sp =>
                new InMemoryEmailService(sp.GetRequiredService<ILogger<InMemoryEmailService>>()));
            services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<InMemoryEmailService>());

            // Enable Magic Link for tests
            services.RemoveAll<MagicLinkConfiguration>();
            services.AddSingleton(new MagicLinkConfiguration { Enabled = true });

            // Provide Email OTP config for tests
            services.RemoveAll<EmailOtpConfiguration>();
            services.AddSingleton(new EmailOtpConfiguration());
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        _host = base.CreateHost(builder);
        return _host;
    }

    /// <summary>
    /// Creates a test user via event stream and returns the UserView.
    /// </summary>
    public async Task<UserView> CreateTestUserAsync(
        string firstname = "Test",
        string lastname = "User",
        string? acronym = "TU",
        string? email = null)
    {
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.CreateVersion7();
        var resolvedAcronym = acronym ?? $"{firstname[0]}{lastname[0]}";
        var resolvedEmail = email ?? $"{firstname.ToLower()}.{lastname.ToLower()}@test.com";

        var @event = new UserCreatedEvent(id, firstname, lastname, resolvedAcronym, resolvedEmail);
        session.Events.StartStream<UserView>(id, @event);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Wait for async projection to create the UserView
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(10));

        var view = await session.LoadAsync<UserView>(id, TestContext.Current.CancellationToken);
        return view!;
    }

    /// <summary>
    /// Creates a test user with full Identity support (UserView + ApplicationUser + password).
    /// This user can log in via POST /api/account/login.
    /// </summary>
    public async Task<UserView> CreateTestUserWithIdentityAsync(
        string firstname = "Test",
        string lastname = "User",
        string? acronym = "TU",
        string? email = null,
        string password = "TestPass1234",
        List<string>? permissions = null)
    {
        // Step 1: Create UserView via event stream
        var userView = await CreateTestUserAsync(firstname, lastname, acronym, email);
        var userName = (acronym ?? $"{firstname[0]}{lastname[0]}").ToLowerInvariant();

        // Step 2: Apply identity setup event (sets UserName + IsActive on UserView)
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(userView.Id, new UserIdentitySetupEvent(userView.Id, userName, true));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(10));

        // Step 3: Create ApplicationUser with password via Identity
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var appUser = new ApplicationUser(userName, email ?? $"{firstname.ToLower()}@test.com")
        {
            Id = userView.Id,
            Firstname = firstname,
            Lastname = lastname,
            Acronym = acronym,
            IsActive = true
        };
        var result = await userManager.CreateAsync(appUser, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create test user identity: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        // Step 4: Grant permissions if specified — wrap in a throwaway role + group,
        // since direct user→permission grants no longer exist in the system.
        if (permissions is { Count: > 0 })
        {
            var role = new PermissionRole
            {
                Id = Guid.CreateVersion7(),
                Name = $"TestRole_{userView.Id:N}",
                ResourceType = "app",
                Permissions = permissions.ToList(),
            };
            session.Store(role);
            session.Events.StartStream(role.Id,
                new PermissionRoleCreatedEvent(role.Id, role.Name, null, role.ResourceType, role.Permissions));

            var group = new Group
            {
                Id = Guid.CreateVersion7(),
                Name = $"TestGroup_{userView.Id:N}",
                MemberIds = [userView.Id],
                RoleIds = [role.Id],
            };
            session.Store(group);
            session.Events.StartStream(group.Id,
                new GroupCreatedEvent(group.Id, group.Name, group.Description,
                    group.MemberIds, group.RoleIds, group.AccessScripts));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Reload with updated UserName
        return (await session.LoadAsync<UserView>(userView.Id, TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Creates a test permission role and returns it.
    /// </summary>
    public async Task<PermissionRole> CreateTestRoleAsync(
        string name,
        string resourceType,
        List<string> permissions,
        string? description = null)
    {
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new PermissionRole
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            ResourceType = resourceType,
            Permissions = permissions
        };
        session.Store(role);
        session.Events.StartStream(role.Id,
            new PermissionRoleCreatedEvent(role.Id, name, description, resourceType, permissions));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role;
    }

    /// <summary>
    /// Creates a test authorization group with members, roles, and access scripts.
    /// </summary>
    public async Task<Group> CreateTestGroupAsync(
        string name,
        List<Guid> memberIds,
        List<Guid>? roleIds = null,
        List<ResourceAccessScript>? accessScripts = null,
        string? description = null)
    {
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var group = new Group
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            MemberIds = memberIds,
            RoleIds = roleIds ?? [],
            AccessScripts = accessScripts ?? []
        };
        session.Store(group);
        session.Events.StartStream(group.Id,
            new GroupCreatedEvent(group.Id, name, description,
                memberIds, roleIds ?? [], accessScripts ?? []));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return group;
    }

    /// <summary>
    /// Builds a ResourceAccessScript with compiled JavaScript (for tests, source = compiled).
    /// </summary>
    public static ResourceAccessScript BuildAccessScript(string resourceType, string compiledJavaScript)
    {
        return new ResourceAccessScript
        {
            ResourceType = resourceType,
            Script = compiledJavaScript,
            CompiledScript = compiledJavaScript
        };
    }

    /// <summary>
    /// Resets all Marten data between tests by stopping the async daemon,
    /// clearing data, and restarting the daemon.
    /// </summary>
    public async Task ResetMartenDataAsync()
    {
        if (_host is null)
        {
            throw new InvalidOperationException(
                "Test IHost is not available. Ensure CreateClient() was called before resetting Marten data.");
        }

        await _host.ResetAllMartenDataAsync();
    }

    /// <summary>
    /// Waits for all async projections to catch up with the latest events.
    /// </summary>
    public async Task WaitForProjectionsAsync(TimeSpan? timeout = null)
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.WaitForNonStaleProjectionDataAsync(timeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Gets a document by ID directly from the database
    /// </summary>
    public async Task<T?> GetDocumentAsync<T>(Guid id) where T : class
    {
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.LoadAsync<T>(id, TestContext.Current.CancellationToken);
    }
}
