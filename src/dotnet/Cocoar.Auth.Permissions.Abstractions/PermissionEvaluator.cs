namespace Cocoar.Auth.Permissions;

/// <summary>
/// Pure permission-check logic with no I/O dependencies — the same evaluator
/// is used IdP-side (by <c>PermissionService</c> in the Authorization slice)
/// and RS-side (by the <c>Cocoar.Auth.Client.AspNetCore</c> helper lib).
/// Lives in <c>Cocoar.Auth.Permissions.Abstractions</c> so external resource
/// servers can reuse it without pulling in Marten/Wolverine/JsEval.
///
/// <para>Permission strings within an App are 2-segment
/// <c>"&lt;resource&gt;:&lt;action&gt;"</c>. The App context is implicit from the
/// caller (the IdP itself for in-process gates, the authenticated RS for
/// distribution-API calls). The bypass tiers collapse common admin grants
/// to fewer entries.</para>
///
/// <para>Evaluation order:</para>
/// <list type="number">
///   <item><b>Realm-wide bypass:</b> grant <c>realm:admin</c> → always true.</item>
///   <item><b>Exact match:</b> the requested permission appears verbatim → true.</item>
///   <item><b>Resource-wide bypass:</b> for permission <c>r:a</c>, grant <c>r:admin</c> → true.</item>
///   <item>Otherwise → false.</item>
/// </list>
/// </summary>
public static class PermissionEvaluator
{
    /// <summary>The realm-wide bypass permission. Holding it grants every check in the realm.</summary>
    public const string RealmAdminPermission = "realm:admin";

    /// <summary>The conventional admin action used for the resource-wide bypass.</summary>
    public const string AdminAction = "admin";

    public static bool Evaluate(IReadOnlyCollection<string> grants, string permission)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentException.ThrowIfNullOrEmpty(permission);

        if (grants.Contains(RealmAdminPermission))
            return true;

        if (grants.Contains(permission))
            return true;

        // Resource-wide bypass kicks in for the canonical 2-segment shape
        // "<resource>:<action>". Permissions outside that shape only pass
        // via realm-admin or exact match.
        var parts = permission.Split(':');
        if (parts.Length == 2)
        {
            var resourceAdmin = $"{parts[0]}:{AdminAction}";
            if (grants.Contains(resourceAdmin))
                return true;
        }

        return false;
    }
}
