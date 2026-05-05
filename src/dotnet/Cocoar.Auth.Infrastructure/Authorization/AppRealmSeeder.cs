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
    /// Resources owned by the system app. Realm-internal stuff only — the
    /// cross-realm `realm:read|write` resource lives on the separate
    /// <see cref="AppSlugs.ControlPlane"/> app, seeded only into the
    /// Control-Plane realm's tenant DB.
    /// </summary>
    private static readonly string[] CocoarAuthResources =
    [
        "app",
        "user",
        "role",
        "authorization-group",
        "permission-role",
        "session",
        "auth-log",
        "gdpr",
        "oauth",
        "oauth-client",
        "oauth-scope",
        "oauth-api",
        "login-provider",
    ];

    /// <summary>
    /// Resources owned by the Control-Plane app. ONLY seeded into the
    /// Control-Plane realm's tenant DB (see <see cref="SeedAsync"/>'s
    /// <paramref name="isControlPlane"/> arg). Tenant realms don't get
    /// the App registration, so a permission like
    /// <c>control-plane:realm:write</c> is unreachable from a tenant
    /// even before the routing-gate fires.
    /// </summary>
    private static readonly string[] ControlPlaneResources =
    [
        "realm",
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
            resources: CocoarAuthResources,
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
                resources: ControlPlaneResources,
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
        string[] resources,
        ILogger? logger,
        string tenantId,
        CancellationToken ct)
    {
        var existing = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == slug && !a.IsDeleted, ct);
        if (existing is not null) return;

        var id = Guid.NewGuid();
        var created = new AppCreatedEvent(
            Id: id,
            Slug: slug,
            DisplayName: displayName,
            Description: description,
            Resources: [.. resources],
            IsSystem: true);

        session.Events.StartStream<App>(id, created);

        logger?.LogInformation(
            "Seeded system app '{Slug}' for tenant '{TenantId}'",
            slug, tenantId);
    }
}
