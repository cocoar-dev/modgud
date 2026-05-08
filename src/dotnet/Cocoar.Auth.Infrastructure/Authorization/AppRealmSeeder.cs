using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Authorization;

/// <summary>
/// Seeds the system <see cref="App"/> (<c>cocoar-auth</c>) into a tenant
/// database. Called from <c>RealmProvisioningService</c> on new-realm
/// creation and from app bootstrap for the system realm. Idempotent —
/// re-running has no effect once seeded.
///
/// <para>The cocoar-auth app is the namespace under which the IAM's own
/// resources (user, oauth-client, session, …) live. Other apps (e.g.
/// <c>timetodo</c>) are registered later via the admin surface.</para>
/// </summary>
public static class AppRealmSeeder
{
    /// <summary>
    /// Permission catalog for the system app. Each tuple is
    /// (<c>resource</c>, <c>action[]</c>) and expands to one
    /// <see cref="AppPermission"/> per action. These are the permissions the
    /// IAM admin surface gates on (see <c>RequiresPermission(...)</c> calls
    /// in the admin endpoints) — adding a new gate without a matching catalog
    /// entry means the permission is unreachable through the role-grant UI.
    /// </summary>
    private static readonly (string Resource, string[] Actions)[] CocoarAuthCatalog =
    [
        // Apps themselves — the realm-admin surface for registering and
        // editing Application records (one per Cocoar SaaS app onboarded
        // into this realm). The system app cocoar-auth is seeded
        // automatically and cannot be deleted.
        ("app", ["admin", "read", "write"]),

        // Identity / directory
        ("user", ["read", "write"]),
        ("role", ["read", "write"]),
        ("authorization-group", ["read", "write"]),
        ("permission-role", ["read", "write"]),

        // Sessions + audit
        ("session", ["read", "write"]),
        ("auth-log", ["read"]),

        // GDPR (permanent-erase only — self-service is implicit on the caller)
        ("gdpr", ["admin"]),

        // OAuth admin surface
        ("oauth", ["admin"]),
        ("oauth-client", ["read", "write"]),
        ("oauth-scope", ["read", "write"]),
        ("oauth-api", ["read", "write"]),

        // Login providers (the configurable buttons on the login page)
        ("login-provider", ["admin", "read", "write"]),
    ];

    /// <summary>
    /// Permission catalog for the Control-Plane app. ONLY seeded into the
    /// Control-Plane realm's tenant DB (see <see cref="SeedAsync"/>'s
    /// <paramref name="isControlPlane"/> arg). Tenant realms don't get the
    /// App registration, so a permission like <c>realm:write</c> is
    /// unreachable from a tenant even before the routing-gate fires.
    /// </summary>
    private static readonly (string Resource, string[] Actions)[] ControlPlaneCatalog =
    [
        ("realm", ["read", "write"]),
    ];

    public static async Task SeedAsync(
        IServiceProvider services,
        string tenantId,
        bool isControlPlane,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);

        await SeedAppIfMissingAsync(
            session,
            slug: AppSlugs.CocoarAuth,
            displayName: "Cocoar.Auth",
            description: "Identity provider — the system app. Owns realm-internal resources (users, sessions, OAuth, …).",
            catalog: CocoarAuthCatalog,
            logger: logger,
            tenantId: tenantId,
            ct: ct);

        if (isControlPlane)
        {
            await SeedAppIfMissingAsync(
                session,
                slug: AppSlugs.ControlPlane,
                displayName: "Control Plane",
                description: "Cross-realm administration surface. Hosts realm-management resources; mounted only on the Control-Plane hostname.",
                catalog: ControlPlaneCatalog,
                logger: logger,
                tenantId: tenantId,
                ct: ct);
        }

        await session.SaveChangesAsync(ct);
    }

    private static async Task SeedAppIfMissingAsync(
        IDocumentSession session,
        string slug,
        string displayName,
        string description,
        (string Resource, string[] Actions)[] catalog,
        ILogger? logger,
        string tenantId,
        CancellationToken ct)
    {
        var existing = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == slug && !a.IsDeleted, ct);
        if (existing is not null) return;

        var permissions = catalog
            .SelectMany(entry => entry.Actions.Select(action =>
                new AppPermission(Guid.NewGuid(), entry.Resource, action, Description: null)))
            .ToList();

        var id = Guid.NewGuid();
        var created = new AppCreatedEvent(
            Id: id,
            Slug: slug,
            DisplayName: displayName,
            Description: description,
            Permissions: permissions,
            IsSystem: true);

        session.Events.StartStream<App>(id, created);

        logger?.LogInformation(
            "Seeded system app '{Slug}' for tenant '{TenantId}' with {PermissionCount} permission(s)",
            slug, tenantId, permissions.Count);
    }
}
