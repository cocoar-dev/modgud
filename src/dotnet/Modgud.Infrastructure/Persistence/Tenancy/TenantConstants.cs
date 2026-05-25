namespace Modgud.Infrastructure.Persistence.Tenancy;

public static class TenantConstants
{
    /// <summary>
    /// Tenant ID used for the system (master) realm — points to the master DB.
    /// Used as fallback when no <c>HttpContext</c> is available (background
    /// services, hosted services, tests without a request scope).
    /// </summary>
    public const string SystemTenantId = "system";

    /// <summary>
    /// HttpContext.Items key under which the resolved tenant slug is stored
    /// by <c>RealmMiddleware</c>.
    /// </summary>
    public const string HttpContextTenantIdKey = "TenantId";

    /// <summary>
    /// HttpContext.Items key under which the resolved <c>TenantInfo</c> record
    /// is stored by <c>RealmMiddleware</c>.
    /// </summary>
    public const string HttpContextTenantInfoKey = "TenantInfo";
}
