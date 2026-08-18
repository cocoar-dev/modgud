using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modgud.Infrastructure.Authorization;

/// <summary>
/// Seeds the system <see cref="App"/> (<c>modgud</c>) into a tenant
/// database. Called from <c>RealmProvisioningService</c> on new-realm
/// creation and from app bootstrap for the system realm. Idempotent —
/// re-running has no effect once seeded.
///
/// <para>The modgud app is the namespace under which the IAM's own
/// resources (user, oauth-client, session, …) live. Downstream apps
/// (e.g. <c>acme-tasks</c>) are registered later via the admin surface.</para>
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
    private static readonly (string Resource, string[] Actions)[] ModgudCatalog =
    [
        // Apps themselves — the realm-admin surface for registering and
        // editing Application records (one per Cocoar SaaS app onboarded
        // into this realm). The system app modgud is seeded
        // automatically and cannot be deleted.
        ("app", ["admin", "read", "write"]),
        ("app-scope", ["read"]),

        // Identity / directory
        ("user", ["read", "write"]),
        ("service-account", ["read", "write"]),
        ("position", ["read", "write"]),
        ("position-terminal", ["enroll"]),
        ("staffing-session", ["read", "force-lock"]),
        ("role", ["read", "write"]),
        ("authorization-group", ["read", "write"]),
        ("permission-role", ["read", "write"]),

        // Sessions + audit. auth-log:read = this realm's security/ops store;
        // audit-log:read = the per-realm GDPR-audit (event-sourced) — two surfaces.
        ("session", ["read", "write"]),
        ("auth-log", ["read"]),
        ("audit-log", ["read"]),

        // GDPR (permanent-erase only — self-service is implicit on the caller)
        ("gdpr", ["admin"]),

        // OAuth admin surface
        ("oauth", ["admin"]),
        ("oauth-client", ["read", "write"]),
        ("oauth-scope", ["read", "write"]),
        ("oauth-api", ["read", "write"]),

        // Login providers (the configurable buttons on the login page)
        ("login-provider", ["admin", "read", "write"]),

        // Per-realm settings (Self-Reg, DCR, Branding-tab — tenant-admin owned
        // config under /admin/realm-settings + /plattform/branding).
        ("realm-settings", ["read", "write"]),

        // Asset library — BYTEA-stored logos / favicons / page-builder media
        // under /plattform/customization/assets.
        ("asset", ["read", "write"]),

        // Operator-facing observability dashboard (/plattform/observability).
        ("observability", ["read"]),

        // Scheduled jobs (Quartz-based system jobs — DCR-GC, history-retention, ...)
        ("scheduled-job", ["read", "write"]),

        // Inbox retention policy (admin-tunable per-kind config under /plattform/inbox-settings).
        // The user-facing /api/inbox endpoints are NOT gated by this — every authenticated
        // user can see their own inbox items.
        ("inbox-settings", ["read", "write"]),
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
        ("platform-audit", ["read"]),
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
            slug: AppSlugs.Modgud,
            displayName: "Modgud",
            description: "Identity provider — the system app. Owns realm-internal resources (users, sessions, OAuth, …).",
            catalog: ModgudCatalog,
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

        if (existing is null)
        {
            // First-time seed — new realm, no App yet.
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
            return;
        }

        // Evolving seed — App already exists. Compare the catalog against the
        // App's current permissions; append any missing (resource, action)
        // pairs via AppUpdatedEvent so the role-grant UI surfaces newly-added
        // gates without manual operator intervention. Existing permissions
        // keep their stable Id; we only ADD here, never delete or rename.
        var currentByKey = existing.Permissions
            .ToDictionary(p => (p.Resource, p.Action), p => p);

        var missing = catalog
            .SelectMany(entry => entry.Actions.Select(action => (entry.Resource, Action: action)))
            .Where(k => !currentByKey.ContainsKey(k))
            .Select(k => new AppPermission(Guid.NewGuid(), k.Resource, k.Action, Description: null))
            .ToList();

        if (missing.Count == 0) return;

        var merged = existing.Permissions.Concat(missing).ToList();

        session.Events.Append(existing.Id, new AppUpdatedEvent(
            Id: existing.Id,
            DisplayName: existing.DisplayName,
            Description: existing.Description,
            Permissions: merged));

        logger?.LogInformation(
            "Evolved system app '{Slug}' for tenant '{TenantId}' — added {MissingCount} permission(s): {Missing}",
            slug, tenantId, missing.Count,
            string.Join(", ", missing.Select(p => $"{p.Resource}:{p.Action}")));
    }
}
