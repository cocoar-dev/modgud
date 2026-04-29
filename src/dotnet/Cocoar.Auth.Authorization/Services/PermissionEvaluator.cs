namespace Cocoar.Auth.Authorization.Services;

/// <summary>
/// Pure permission-check logic, separated from <see cref="PermissionService"/> so
/// it can be unit-tested without a Marten session.
///
/// Permission strings are fully qualified as <c>"appSlug:resource:action"</c>.
/// Bypass shortcuts collapse common admin grants to fewer entries.
///
/// Evaluation order:
/// <list type="number">
///   <item><b>Realm-wide bypass:</b> grant <c>realm:admin</c> → always true.</item>
///   <item><b>Exact match:</b> the requested permission appears verbatim → true.</item>
///   <item><b>App-wide bypass:</b> for permission <c>a:b:c</c>, grant <c>a:admin</c> → true.</item>
///   <item><b>Resource-wide bypass:</b> for permission <c>a:b:c</c>, grant <c>a:b:admin</c> → true.</item>
///   <item>Otherwise → false.</item>
/// </list>
/// </summary>
public static class PermissionEvaluator
{
    /// <summary>The realm-wide bypass permission. Holding it grants every check in the realm.</summary>
    public const string RealmAdminPermission = "realm:admin";

    /// <summary>The conventional admin action used for app-wide and resource-wide bypasses.</summary>
    public const string AdminAction = "admin";

    public static bool Evaluate(IReadOnlyCollection<string> grants, string permission)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentException.ThrowIfNullOrEmpty(permission);

        if (grants.Contains(RealmAdminPermission))
            return true;

        if (grants.Contains(permission))
            return true;

        // Structural bypasses only kick in for the canonical 3-segment shape
        // "<app>:<resource>:<action>". Permissions outside that shape only
        // pass via realm-admin or exact match.
        var parts = permission.Split(':');
        if (parts.Length == 3)
        {
            var appAdmin = $"{parts[0]}:{AdminAction}";
            if (grants.Contains(appAdmin))
                return true;

            var resourceAdmin = $"{parts[0]}:{parts[1]}:{AdminAction}";
            if (grants.Contains(resourceAdmin))
                return true;
        }

        return false;
    }
}
