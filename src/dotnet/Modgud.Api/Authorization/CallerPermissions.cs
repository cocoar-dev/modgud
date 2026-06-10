using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Permissions;

namespace Modgud.Api.Authorization;

/// <summary>
/// Helpers for in-endpoint authorization checks that go beyond the single
/// <c>.RequiresPermission(...)</c> gate — specifically the
/// "only a realm:admin may confer realm:admin" privilege-escalation guard,
/// which needs to know the caller's own realm-admin status at the point a
/// write would grant it.
/// </summary>
public static class CallerPermissions
{
    /// <summary>
    /// True if the authenticated caller holds the realm-wide
    /// <c>realm:admin</c> bypass in the current (tenant-scoped) realm. Fails
    /// closed: an unauthenticated or unresolved caller is treated as NOT a
    /// realm admin. Resolution runs against the request-scoped, tenant-bound
    /// <see cref="IPermissionService"/>, so it reflects the live role graph of
    /// the current realm — not a cached or cross-realm principal.
    /// </summary>
    public static async Task<bool> IsRealmAdminAsync(
        HttpContext http,
        IPermissionService permissionService,
        CancellationToken ct = default)
    {
        var userId = http.GetUserId();
        if (userId is null) return false;

        return await permissionService.HasPermissionAsync(
            userId.Value, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct);
    }
}
