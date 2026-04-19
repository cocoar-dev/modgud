namespace Cocoar.Auth.Domain.Authorization;

/// <summary>
/// Compile-time constants for every permission registered in <see cref="ResourceRegistry"/>.
/// Use these instead of magic strings so renames break the build instead of authorization.
/// </summary>
public static class Permissions
{
    public static class User
    {
        public const string Read = "user:read";
        public const string Create = "user:create";
        public const string Update = "user:update";
        public const string Delete = "user:delete";
        public const string Unlock = "user:unlock";
        public const string Impersonate = "user:impersonate";
    }

    public static class Session
    {
        public const string Read = "session:read";
        public const string Revoke = "session:revoke";
    }

    public static class PermissionRole
    {
        public const string Read = "permission-role:read";
        public const string Create = "permission-role:create";
        public const string Update = "permission-role:update";
        public const string Delete = "permission-role:delete";
    }

    public static class AuthorizationGroup
    {
        public const string Read = "authorization-group:read";
        public const string Create = "authorization-group:create";
        public const string Update = "authorization-group:update";
        public const string Delete = "authorization-group:delete";
        public const string ManageMembers = "authorization-group:manage-members";
        public const string ManageRoles = "authorization-group:manage-roles";
        public const string EditScripts = "authorization-group:edit-scripts";
    }

    public static class OAuthClient
    {
        public const string Read = "oauth-client:read";
        public const string Create = "oauth-client:create";
        public const string Update = "oauth-client:update";
        public const string Delete = "oauth-client:delete";
    }

    public static class OAuthScope
    {
        public const string Read = "oauth-scope:read";
        public const string Create = "oauth-scope:create";
        public const string Update = "oauth-scope:update";
        public const string Delete = "oauth-scope:delete";
    }

    public static class OAuthApi
    {
        public const string Read = "oauth-api:read";
        public const string Create = "oauth-api:create";
        public const string Update = "oauth-api:update";
        public const string Delete = "oauth-api:delete";
    }

    public static class LoginProvider
    {
        public const string Read = "login-provider:read";
        public const string Create = "login-provider:create";
        public const string Update = "login-provider:update";
        public const string Delete = "login-provider:delete";
    }

    public static class Realm
    {
        public const string Read = "realm:read";
        public const string Create = "realm:create";
        public const string Update = "realm:update";
        public const string Delete = "realm:delete";
    }

    public static class AuditLog
    {
        public const string Read = "audit-log:read";
    }

    /// <summary>
    /// Bypass-all permission within a single realm. Granted via group membership
    /// inside that realm. Holds for everything in that realm; does not cross
    /// realm boundaries.
    /// </summary>
    public const string TenantAdmin = "tenant:admin";

    /// <summary>
    /// Bypass-all permission across all realms. Only meaningful when granted
    /// inside the system realm. The first user in the system realm receives
    /// this via the bootstrap "System Administrators" group.
    /// </summary>
    public const string SystemAdmin = "system:admin";
}
