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
using Modgud.Domain.Common;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Domain.Users.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Api;
using Modgud.Authentication.Api.Account;
using Modgud.Infrastructure.Email;
using Modgud.Authentication.Identity;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Modgud.Infrastructure.Scheduling;
using Marten.Storage;
using Npgsql;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Created once in SharedPostgresFixture and reused across all tests.
/// Test configuration is applied via CocoarTestConfiguration.Apply() before creation.
/// </summary>
public class ModgudWebApplicationFactory : WebApplicationFactory<Program>
{
    private IHost? _host;
    private readonly bool _enableDirectScopeTenantFallback;
    protected virtual bool ProvisionLegacySystemRealm => true;

    public ModgudWebApplicationFactory(SharedPostgresFixture fixture)
    {
        _enableDirectScopeTenantFallback = true;
        // No configuration needed here - CocoarTestConfiguration.Apply()
        // was already called in the fixture
    }

    /// <summary>
    /// Parameterless ctor for derived factories (e.g. the cold-start harness)
    /// that drive their own configuration context instead of binding to the
    /// shared <see cref="SharedPostgresFixture"/>.
    /// </summary>
    protected ModgudWebApplicationFactory()
    {
        _enableDirectScopeTenantFallback = false;
    }

    /// <summary>
    /// Test seam for CIMD: maps a CIMD <c>client_id</c> URL to the
    /// raw JSON metadata document the stubbed fetcher should return. Tests
    /// populate this instead of standing up a real HTTPS endpoint; the named
    /// CIMD HttpClient's primary handler is overridden (below) to serve from
    /// here, bypassing the real SSRF-guarded transport (SSRF is unit-tested
    /// separately). A URL with no entry yields 404 → resolve fails.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> CimdDocuments { get; } = new();

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
            // Production deliberately refuses tenant-scoped sessions when no
            // request or explicit TenantContext exists. Most legacy
            // integration tests also arrange/assert through direct DI scopes,
            // so give those scopes an explicit test tenant without restoring a
            // production fallback. Real HTTP requests still replace this
            // accessor's ambient context for the duration of the request.
            if (_enableDirectScopeTenantFallback)
            {
                services.RemoveAll<IHttpContextAccessor>();
                services.AddSingleton<IHttpContextAccessor>(
                    new TestTenantHttpContextAccessor(TenantConstants.SystemTenantId));
            }

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

            // SecurityStamp revalidation — make every authenticated request a
            // stamp-revalidation test. Production ValidationInterval is 5 min
            // (Program.cs), so a follow-up request right after sign-in lands
            // inside the cache window and never re-validates — which structurally
            // MASKS any sign-in path that mints a cookie WITHOUT the user's
            // authoritative security stamp. Forcing Zero closes that blind spot:
            // the affected non-password paths (magic-link / passkey / OIDC / SAML)
            // now fail their post-sign-in assertions, and any future sign-in path
            // that regresses the same way fails its own happy-path test.
            // Password-based logins re-align the in-memory stamp via FindByName
            // before minting, so they stay green.
            services.Configure<SecurityStampValidatorOptions>(options =>
            {
                options.ValidationInterval = TimeSpan.Zero;
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

            // CIMD: override the metadata fetcher's primary handler
            // with an in-memory stub serving from CimdDocuments, so tests
            // exercise the full resolve→synthesize→token flow without real
            // outbound HTTP. The last ConfigurePrimaryHttpMessageHandler wins.
            services.AddHttpClient(Modgud.Infrastructure.OpenIddict.Cimd.CimdClientResolver.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubCimdHandler(CimdDocuments));
        });
    }

    private sealed class TestTenantHttpContextAccessor(string tenantId) : IHttpContextAccessor
    {
        private readonly AsyncLocal<HttpContext?> _current = new();
        private readonly HttpContext _fallback = CreateFallbackContext(tenantId);

        public HttpContext? HttpContext
        {
            get => _current.Value ?? _fallback;
            set => _current.Value = value;
        }

        private static HttpContext CreateFallbackContext(string tenantId)
        {
            var context = new DefaultHttpContext();
            context.Items[TenantConstants.HttpContextTenantIdKey] = tenantId;
            return context;
        }
    }

    /// <summary>In-memory stand-in for the CIMD metadata endpoint. Returns the
    /// document registered for the exact request URL, or 404.</summary>
    private sealed class StubCimdHandler(
        System.Collections.Concurrent.ConcurrentDictionary<string, string> documents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (documents.TryGetValue(url, out var json))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        _host = base.CreateHost(builder);
        if (ProvisionLegacySystemRealm)
            ProvisionLegacyTestRealmAsync(_host.Services).GetAwaiter().GetResult();
        return _host;
    }

    /// <summary>
    /// Most pre-installation integration tests historically use a tenant named
    /// "system". Production no longer creates that realm implicitly, so the
    /// test harness provisions it explicitly. Fresh-installation tests opt out
    /// through <see cref="UninitializedModgudWebApplicationFactory"/>.
    /// </summary>
    private static async Task ProvisionLegacyTestRealmAsync(IServiceProvider services)
    {
        var masterCs = services.GetRequiredService<IMasterConnectionString>().Value;
        var systemDbName =
            $"{new NpgsqlConnectionStringBuilder(masterCs).Database}_{TenantConstants.SystemTenantId}";
        var systemCs = new NpgsqlConnectionStringBuilder(masterCs)
        {
            Database = systemDbName,
        }.ConnectionString;

        var adminCs = new NpgsqlConnectionStringBuilder(masterCs) { Database = "postgres" };
        await using (var connection = new NpgsqlConnection(adminCs.ConnectionString))
        {
            await connection.OpenAsync();
            await using var exists = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @name", connection);
            exists.Parameters.AddWithValue("name", systemDbName);
            if (await exists.ExecuteScalarAsync() is null)
            {
                var quoted = "\"" + systemDbName.Replace("\"", "\"\"") + "\"";
#pragma warning disable CA2100
                await using var create = new NpgsqlCommand($"CREATE DATABASE {quoted}", connection);
#pragma warning restore CA2100
                await create.ExecuteNonQueryAsync();
            }
        }

        var store = services.GetRequiredService<IDocumentStore>();
        var tenancy = (MasterTableTenancy)store.Options.Tenancy;
        await tenancy.AddDatabaseRecordAsync(TenantConstants.SystemTenantId, systemCs);
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await services.GetRequiredService<IRealmMessageStorageProvisioner>()
            .EnsureProvisionedAsync(TenantConstants.SystemTenantId);

        await using var scope = services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IRealmProvisioningService>();
        await provisioning.EnsureSystemRealmExistsAsync();
        await Modgud.Infrastructure.OAuth.OAuthRealmSeeder.SeedAsync(
            scope.ServiceProvider, TenantConstants.SystemTenantId);
        await scope.ServiceProvider
            .GetRequiredService<Modgud.Application.Services.ILoginProviderRealmSeeder>()
            .SeedAsync(TenantConstants.SystemTenantId);
        await Modgud.Infrastructure.Authorization.AppRealmSeeder.SeedAsync(
            scope.ServiceProvider,
            TenantConstants.SystemTenantId,
            isControlPlane: true);
        await scope.ServiceProvider.GetRequiredService<IRealmCache>().InitializeAsync();
        await scope.ServiceProvider.GetRequiredService<IRealmJobScheduleObserver>().ReconcileAsync();
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
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.CreateVersion7();
        var resolvedAcronym = acronym ?? $"{firstname[0]}{lastname[0]}";
        var resolvedEmail = email ?? $"{firstname.ToLower()}.{lastname.ToLower()}@test.com";

        var @event = new UserCreatedEvent(id, firstname, lastname, resolvedAcronym, resolvedEmail);
        session.Events.StartStream<UserView>(id, @event);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Deterministically materialize the async UserView via the catch-up, then read it.
        // Verified against Marten V9.8.0 / JasperFx V2.12.0 source: the catch-up path
        // (ForceAllMartenDaemonActivityToCatchUpAsync -> JasperFxAsyncDaemon.CatchUpAsync)
        // re-runs _highWater.CheckNowAsync() (fresh DB detection) then
        // SubscriptionAgent.CatchUpAsync, which has NO seq<=1 guard (that guard lives ONLY
        // in the WaitForNonStale* helpers, which we don't use). So even the FIRST user right
        // after ResetAllMartenDataAsync (events sequence RESTART WITH 1 -> seq_id == 1) IS
        // applied (state.Sequence 0 != highWaterMark 1). A null here is therefore a GENUINE
        // catch-up failure, not an expected empty-state no-op — fail loud AT THE SOURCE
        // rather than fabricating a partial UserView that resurfaces as a displaced flake
        // (e.g. a /connect/userinfo 401) in some later test. Mirrors CreateTestUserWithIdentityAsync.
        await CatchUpAsyncProjectionsAsync();

        return await session.LoadAsync<UserView>(id, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException(
                $"UserView {id} not materialized after async-projection catch-up " +
                "(the JasperFx CatchUp path applies seq_id 1, so this is a real daemon " +
                "catch-up failure, not an expected no-op).");
    }

    /// <summary>
    /// Creates a test user with full Identity support (UserView + ApplicationUser + password).
    /// This user can log in via POST /api/account/login.
    ///
    /// <para>The legacy <c>permissions: ["realm:admin"]</c> pattern is replaced
    /// by the explicit <paramref name="isRealmAdmin"/> flag — pass true to
    /// attach the user to a System Admin role + Administrators wildcard-
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
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(userView.Id, new UserIdentitySetupEvent(userView.Id, userName, true));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        // The async UserView is materialized by the single catch-up at the end of
        // this method (after the admin events are also appended).

        // Step 3: Create ApplicationUser with password via Identity
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var appUser = new ApplicationUser(userName, email ?? $"{firstname.ToLower()}@test.com")
        {
            Id = userView.Id,
            Firstname = firstname,
            Lastname = lastname,
            Acronym = acronym,
            IsActive = true,
            // Pre-confirm email so endpoints gated by the Phase-2B email-
            // verification filter (ProfileEndpoints, EmailOtpEndpoints, ...)
            // are reachable from test users. Mirrors RealmAdminBootstrapper's
            // production-side pre-confirm rationale: a test-created user is
            // out-of-band proof of identity, same as the CLI/invite-mode
            // bootstrap-admin.
            EmailConfirmed = true,
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
            // PermissionRole + Principal (= polymorphic Group/Person) both have
            // inline SingleStreamProjections that build the doc from their
            // Create-event. Marten 8.34+ tightened optimistic-concurrency
            // detection — a direct session.Store(...) alongside the same
            // SaveChangesAsync as the StartStream now conflicts with the
            // projection-emitted upsert. Trust the projection, only emit the
            // event: the inline projection writes the doc synchronously
            // during SaveChangesAsync so post-save reads see it immediately.
            var roleId = Guid.CreateVersion7();
            var roleName = $"TestSystemAdmin_{userView.Id:N}";
            session.Events.StartStream(roleId,
                new PermissionRoleCreatedEvent(roleId, roleName, null,
                    null, true, []));

            var groupId = Guid.CreateVersion7();
            var groupName = $"TestAdmins_{userView.Id:N}";
            session.Events.StartStream(groupId,
                new GroupCreatedEvent(groupId, groupName, null,
                    [userView.Id], [roleId],
                    BoundTo: ["*"]));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Deterministically materialize the async UserView (now reflecting the
        // identity-setup event) before returning.
        await CatchUpAsyncProjectionsAsync();

        var view = await session.LoadAsync<UserView>(userView.Id, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException(
                $"UserView {userView.Id} not materialized after async-projection catch-up.");
        if (view.UserName != userName)
            throw new InvalidOperationException(
                $"UserView {userView.Id} did not reflect identity setup (UserName '{view.UserName}' != '{userName}') after catch-up.");
        return view;
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
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        var perms = permissions ?? [];

        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        Guid? appId = null;
        var permissionIds = new List<Guid>();
        if (perms.Count > 0)
        {
            var slug = appSlug ?? AppSlugs.Modgud;
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

        // PermissionRole has an inline projection — Events.StartStream is
        // enough; a direct session.Store conflicts under Marten 8.34+.
        var role = new PermissionRole
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            AppId = appId,
            IsRealmAdmin = isRealmAdmin,
            PermissionIds = permissionIds,
        };
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
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Group is a polymorphic Principal — PrincipalProjection.Create(GroupCreatedEvent)
        // writes the doc inline. Same Marten 8.34+ concurrency rule as
        // PermissionRole: emit the event only, skip the direct Store.
        var bound = boundTo ?? [AppSlugs.Modgud];
        var group = new Group
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            MemberIds = memberIds,
            RoleIds = roleIds ?? [],
            BoundTo = bound,
        };
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
    /// roles via <see cref="CreateTestRoleAsync"/> find the modgud
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

        // The singleton RealmKeyStore caches per-realm signing keys in memory for 60s
        // and survives the Marten wipe above — which deleted the persisted
        // RealmSigningKey rows. Without clearing it, a token gets signed with a cached
        // active key (60s fast path, no DB read) whose row no longer exists, while the
        // JWKS verification set is rebuilt from the now-empty DB and omits it -> OpenIddict
        // ID2090 "signing key not found" -> /connect/userinfo 401 (intermittent on CI).
        // Clearing makes the next sign + verify both re-read the regenerated key.
        if (Services.GetService<Modgud.Infrastructure.Realms.IRealmKeyStore>()
                is Modgud.Infrastructure.Realms.RealmKeyStore keyStore)
        {
            keyStore.ClearCachesForReset();
        }

        // Re-seed the system App + Control-Plane App so per-test fresh state
        // still has the catalog. The boot-time seed in Program.cs runs once
        // and is wiped by ResetAllMartenDataAsync; running it again here is
        // idempotent so this stays safe even if the boot seed survives.
        await Modgud.Infrastructure.Authorization.AppRealmSeeder.SeedAsync(
            Services,
            tenantId: "system",
            isControlPlane: true);
    }

    /// <summary>
    /// Deterministically materializes all async projections up to the latest events.
    /// Call after appending events and before reading an async view.
    /// See <see cref="CatchUpAsyncProjectionsAsync"/>.
    /// </summary>
    public Task WaitForProjectionsAsync(TimeSpan? timeout = null)
        => CatchUpAsyncProjectionsAsync(timeout);

    /// <summary>
    /// Gets a document by ID directly from the database
    /// </summary>
    public async Task<T?> GetDocumentAsync<T>(Guid id) where T : class
    {
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.LoadAsync<T>(id, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Deterministically materializes ALL async projections across EVERY tenant DB
    /// using Marten's <c>ForceAllMartenDaemonActivityToCatchUpAsync</c>: it pauses
    /// the projection coordinator, runs an INLINE per-shard catch-up on the calling
    /// thread (independent of the continuously-running daemon, which may still be in
    /// cold-start after the per-test reset on a contended CI runner), then resumes.
    ///
    /// This replaces the flaky <c>WaitForNonStaleProjectionDataAsync</c> + <c>LoadAsync</c>
    /// poll barrier, which depended on the continuous daemon making progress and so
    /// either returned before a just-appended event was applied or timed out under
    /// Marten 9.x master-table multi-tenancy (the Critter-Stack 9.8 CI flake). It is
    /// also multi-tenant-complete (covers the system DB and every realm DB), unlike
    /// the one-database WaitForNonStale overload.
    ///
    /// Call AFTER the arrange events are appended + committed and BEFORE reading an
    /// async view (e.g. <c>UserView</c>, a MultiStreamProjection that cannot be Inline).
    /// </summary>
    private async Task CatchUpAsyncProjectionsAsync(TimeSpan? timeout = null)
    {
        if (_host is null)
            throw new InvalidOperationException(
                "Test IHost is not available. Ensure CreateClient() was called first.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

        var errors = await _host.ForceAllMartenDaemonActivityToCatchUpAsync(cts.Token);
        if (errors.Count > 0)
            throw new AggregateException(
                "Async-projection catch-up failed after Marten reset/append.", errors);
    }
}

/// <summary>
/// Test host that exposes the real production cold-start state: master/global
/// schemas exist, but the realm registry is empty.
/// </summary>
public sealed class UninitializedModgudWebApplicationFactory : ModgudWebApplicationFactory
{
    protected override bool ProvisionLegacySystemRealm => false;
}
