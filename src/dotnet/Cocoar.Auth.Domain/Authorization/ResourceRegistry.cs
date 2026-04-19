namespace Cocoar.Auth.Domain.Authorization;

/// <summary>
/// Static registry of all permission resources and their actions.
/// Permissions follow the format "resource:action".
/// Roles store only actions (e.g., "read") and reference a ResourceType (e.g., "user").
/// The full permission is constructed as "{ResourceType}:{action}".
/// <para>
/// Initialized once at application startup. Permissions referenced in code should
/// match exactly — typos cannot be caught at compile time, so prefer
/// <see cref="Permissions"/> constants where possible.
/// </para>
/// </summary>
public static class ResourceRegistry
{
    private static readonly Dictionary<string, List<string>> Resources = new();

    public static void Initialize()
    {
        // ── Identity ─────────────────────────────────────────────
        Register("user", ["read", "create", "update", "delete", "unlock", "impersonate"]);
        Register("session", ["read", "revoke"]);

        // ── Authorization (ABAC core) ────────────────────────────
        Register("permission-role", ["read", "create", "update", "delete"]);
        Register("authorization-group", ["read", "create", "update", "delete", "manage-members", "manage-roles", "edit-scripts"]);

        // ── OAuth / OpenID Connect ───────────────────────────────
        Register("oauth-client", ["read", "create", "update", "delete"]);
        Register("oauth-scope", ["read", "create", "update", "delete"]);
        Register("oauth-api", ["read", "create", "update", "delete"]);
        Register("login-provider", ["read", "create", "update", "delete"]);

        // ── Infrastructure ───────────────────────────────────────
        Register("realm", ["read", "create", "update", "delete"]);
        Register("audit-log", ["read"]);

        // ── Super-admin scopes ───────────────────────────────────
        // tenant:admin = bypass-all within a single realm (granted via group inside that realm)
        // system:admin = bypass-all across all realms (only meaningful in the "system" realm)
        Register("tenant", ["admin"]);
        Register("system", ["admin"]);
    }

    public static void Register(string resource, List<string> actions)
    {
        Resources[resource] = actions;
    }

    public static bool IsValidPermission(string permission)
    {
        var parts = permission.Split(':', 2);
        if (parts.Length != 2) return false;
        return Resources.TryGetValue(parts[0], out var actions) && actions.Contains(parts[1]);
    }

    public static bool IsValidAction(string resourceType, string action)
    {
        return Resources.TryGetValue(resourceType, out var actions) && actions.Contains(action);
    }

    public static List<string> GetAllPermissions()
    {
        return Resources
            .SelectMany(r => r.Value.Select(a => $"{r.Key}:{a}"))
            .ToList();
    }

    public static List<string> GetActionsForResource(string resource)
    {
        return Resources.TryGetValue(resource, out var actions)
            ? actions.ToList()
            : [];
    }

    public static List<string> GetResourceTypes()
    {
        return Resources.Keys.ToList();
    }
}
