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
    /// Resources owned by the system app. Mirrors the resources currently
    /// registered globally in <c>ResourceRegistry</c> — kept in sync with
    /// <c>Cocoar.Auth.Infrastructure.DependencyInjection</c> until step 5
    /// of the plan moves the registry to be app-aware.
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
        "realm",
        "oauth",
        "oauth-client",
        "oauth-scope",
        "oauth-api",
        "login-provider",
    ];

    public static async Task SeedAsync(IServiceProvider services, string tenantId, ILogger? logger = null, CancellationToken ct = default)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);

        var existing = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == AppSlugs.CocoarAuth && !a.IsDeleted, ct);
        if (existing is not null)
            return;

        var id = Guid.NewGuid();
        var created = new AppCreatedEvent(
            Id: id,
            Slug: AppSlugs.CocoarAuth,
            DisplayName: "Cocoar.Auth",
            Description: "Identity provider — the system app. Owns realm-internal resources (users, sessions, OAuth, …).",
            Resources: [.. CocoarAuthResources],
            IsSystem: true);

        session.Events.StartStream<App>(id, created);
        await session.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Seeded system app '{Slug}' for tenant '{TenantId}'",
            AppSlugs.CocoarAuth, tenantId);
    }
}
