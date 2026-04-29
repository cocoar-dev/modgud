namespace Cocoar.Auth.Authorization.Services;

/// <summary>
/// Pure permission-check logic, separated from <see cref="PermissionService"/> so it
/// can be unit-tested without a Marten session.
///
/// Evaluation order:
/// <list type="number">
///   <item>Global bypass: <c>app:admin</c> in grants → always true.</item>
///   <item>Exact match: requested permission appears verbatim in grants → true.</item>
///   <item>Resource-scoped bypass: for a permission shaped <c>&lt;resource&gt;:&lt;action&gt;</c>,
///         holding <c>&lt;resource&gt;:admin</c> → true. Lets one grant cover every
///         action on a resource without enumerating each.</item>
///   <item>Otherwise → false.</item>
/// </list>
/// </summary>
public static class PermissionEvaluator
{
    /// <summary>The global-bypass permission. Any principal that holds it passes every check.</summary>
    public const string GlobalAdminPermission = "app:admin";

    /// <summary>The per-resource bypass action. Holding <c>X:admin</c> grants every action on resource X.</summary>
    public const string ResourceAdminAction = "admin";

    public static bool Evaluate(IReadOnlyCollection<string> grants, string permission)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentException.ThrowIfNullOrEmpty(permission);

        if (grants.Contains(GlobalAdminPermission))
            return true;

        if (grants.Contains(permission))
            return true;

        var colon = permission.IndexOf(':');
        if (colon > 0)
        {
            var resourceAdmin = string.Concat(permission.AsSpan(0, colon), $":{ResourceAdminAction}");
            if (grants.Contains(resourceAdmin))
                return true;
        }

        return false;
    }
}
