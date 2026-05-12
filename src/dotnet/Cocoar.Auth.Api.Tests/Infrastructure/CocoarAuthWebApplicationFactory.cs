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
using Cocoar.Auth.Authorization.Apps;
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

        // Clear logging providers to avoid Event Log dispose issues on Windows.
        // Console provider re-added so server-side errors surface during test debugging.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureServices(services =>
        {
            // .NET HostOptions.ShutdownTimeout defaults to 5 seconds. That's
            // not enough for Wolverine + Marten + Testcontainer to release
            // Postgres ownership cleanly during teardown — under load on the
            // CI runner the shutdown can take 8-12s, which manifests as a
            // flaky `MessageStoreCollection.ReleaseAllOwnershipAsync` /
            // `OperationCanceledException` in `SharedPostgresFixture.DisposeAsync`
            // (entire test run reports failure even though every test
            // passed). 30s is the .NET-host-builder default for ASP.NET
            // Core apps in current versions; bumping the test host to the
            // same value eliminates the race without affecting production
            // shutdown behaviour.
            services.Configure<HostOptions>(o =>
            {
                o.ShutdownTimeout = TimeSpan.FromSeconds(30);
            });

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
        // Tenant-scoped wait — we run on master-table multi-tenancy, so the helper
        // needs to know which tenant DB to poll. All test data lands in the "system"
        // tenant (see TenantedSessionFactory's HttpContext-less fallback).
        await store.WaitForNonStaleProjectionDataAsync("system", TimeSpan.FromSeconds(10));

        var view = await session.LoadAsync<UserView>(id, TestContext.Current.CancellationToken);
        return view!;
    }

    /// <summary>
    /// Creates a test user with full Identity support (UserView + ApplicationUser + password).
    /// This user can log in via POST /api/account/login.
    ///
    /// <para>The legacy <c>permissions: ["realm:admin"]</c> pattern is replaced
    /// by the explicit <paramref name="isRealmAdmin"/> flag — pass true to
    /// attach the user to a System Admin role + Administratoren wildcard-
    /// bound group so realm-wide permission checks pass. Bare-action grants
    /// against a specific resource catalog must use
    /// <see cref="CreateTestRoleAsync"/> + <see cref="CreateTestGroupAsync"/>
    /// directly.</para>
    /// </summary>
    public async Task<UserView> CreateTestUserWithIdentityAsync(
        string firstname = "Test",
        string lastname = "User",
        string? acronym = "TU",
        string? email = null,
        string password = "TestPass1234",
        bool isRealmAdmin = false)
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
        // Tenant-scoped wait — we run on master-table multi-tenancy, so the helper
        // needs to know which tenant DB to poll. All test data lands in the "system"
        // tenant (see TenantedSessionFactory's HttpContext-less fallback).
        await store.WaitForNonStaleProjectionDataAsync("system", TimeSpan.FromSeconds(10));

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

        // Step 4: Realm-admin shortcut — wrap the user in a wildcard-bound
        // group attached to a System Admin role (IsRealmAdmin=true). Direct
        // user→permission grants don't exist in the model; everything flows
        // via Group → Role → Permission.
        if (isRealmAdmin)
        {
            var role = new PermissionRole
            {
                Id = Guid.CreateVersion7(),
                Name = $"TestSystemAdmin_{userView.Id:N}",
                AppId = null,
                IsRealmAdmin = true,
                PermissionIds = [],
            };
            session.Store(role);
            session.Events.StartStream(role.Id,
                new PermissionRoleCreatedEvent(role.Id, role.Name, null,
                    role.AppId, role.IsRealmAdmin, role.PermissionIds));

            var group = new Group
            {
                Id = Guid.CreateVersion7(),
                Name = $"TestAdmins_{userView.Id:N}",
                MemberIds = [userView.Id],
                RoleIds = [role.Id],
                BoundTo = ["*"],
            };
            session.Store(group);
            session.Events.StartStream(group.Id,
                new GroupCreatedEvent(group.Id, group.Name, group.Description,
                    group.MemberIds, group.RoleIds,
                    BoundTo: group.BoundTo));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Reload with updated UserName
        return (await session.LoadAsync<UserView>(userView.Id, TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Creates a test permission role bound to one App's catalog. Each
    /// <paramref name="permissions"/> tuple is resolved to an
    /// <c>AppPermission.Id</c> in the linked App's catalog at write time —
    /// missing catalog entries throw immediately so test arranges fail fast
    /// rather than producing a silently-empty role. Pass
    /// <paramref name="isRealmAdmin"/> to mark the role as the
    /// realm-admin bypass; in that case <paramref name="appSlug"/> may be
    /// null and <paramref name="permissions"/> must be empty.
    /// </summary>
    public async Task<PermissionRole> CreateTestRoleAsync(
        string name,
        IReadOnlyList<(string Resource, string Action)>? permissions = null,
        string? description = null,
        string? appSlug = null,
        bool isRealmAdmin = false)
    {
        var perms = permissions ?? [];

        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        Guid? appId = null;
        var permissionIds = new List<Guid>();
        if (perms.Count > 0)
        {
            var slug = appSlug ?? AppSlugs.CocoarAuth;
            var app = await session.Query<App>()
                .FirstOrDefaultAsync(a => a.Slug == slug && !a.IsDeleted, TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException(
                    $"CreateTestRoleAsync: App '{slug}' not found in tenant. Seed it first.");
            appId = app.Id;

            foreach (var (resource, action) in perms)
            {
                var entry = app.Permissions.FirstOrDefault(p => p.Resource == resource && p.Action == action)
                    ?? throw new InvalidOperationException(
                        $"CreateTestRoleAsync: '{resource}:{action}' not in App '{slug}' catalog. Add it via AppRealmSeeder or the admin endpoint first.");
                permissionIds.Add(entry.Id);
            }
        }
        else if (!string.IsNullOrEmpty(appSlug))
        {
            // Caller specified an App but no permissions — record the FK
            // anyway so role queries that filter by AppId still find it.
            var app = await session.Query<App>()
                .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted, TestContext.Current.CancellationToken);
            appId = app?.Id;
        }

        var role = new PermissionRole
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            AppId = appId,
            IsRealmAdmin = isRealmAdmin,
            PermissionIds = permissionIds,
        };
        session.Store(role);
        session.Events.StartStream(role.Id,
            new PermissionRoleCreatedEvent(role.Id, name, description, role.AppId, role.IsRealmAdmin, role.PermissionIds));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role;
    }

    /// <summary>
    /// Creates a test authorization group with members and roles.
    /// </summary>
    public async Task<Group> CreateTestGroupAsync(
        string name,
        List<Guid> memberIds,
        List<Guid>? roleIds = null,
        string? description = null,
        List<string>? boundTo = null)
    {
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var bound = boundTo ?? [AppSlugs.CocoarAuth];
        var group = new Group
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            MemberIds = memberIds,
            RoleIds = roleIds ?? [],
            BoundTo = bound,
        };
        session.Store(group);
        session.Events.StartStream(group.Id,
            new GroupCreatedEvent(group.Id, name, description,
                memberIds, roleIds ?? [],
                BoundTo: bound));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return group;
    }

    /// <summary>
    /// Resets all Marten data between tests by stopping the async daemon,
    /// clearing data, and restarting the daemon. After the wipe, the
    /// system <see cref="App"/> catalog is re-seeded so tests that build
    /// roles via <see cref="CreateTestRoleAsync"/> find the cocoar-auth
    /// catalog they need to FK into.
    /// </summary>
    public async Task ResetMartenDataAsync()
    {
        if (_host is null)
        {
            throw new InvalidOperationException(
                "Test IHost is not available. Ensure CreateClient() was called before resetting Marten data.");
        }

        await _host.ResetAllMartenDataAsync();

        // Re-seed the system App + Control-Plane App so per-test fresh state
        // still has the catalog. The boot-time seed in Program.cs runs once
        // and is wiped by ResetAllMartenDataAsync; running it again here is
        // idempotent so this stays safe even if the boot seed survives.
        await Cocoar.Auth.Infrastructure.Authorization.AppRealmSeeder.SeedAsync(
            Services,
            tenantId: "system",
            isControlPlane: true);
    }

    /// <summary>
    /// Waits for all async projections to catch up with the latest events.
    /// </summary>
    public async Task WaitForProjectionsAsync(TimeSpan? timeout = null)
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.WaitForNonStaleProjectionDataAsync("system", timeout ?? TimeSpan.FromSeconds(10));
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
