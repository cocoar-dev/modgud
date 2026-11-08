namespace Modgud.Infrastructure.Persistence.Tenancy;

public static class TenantConstants
{
    /// <summary>
    /// Tenant ID of the bootstrap/system realm. It has its OWN physical
    /// database <c>{master}_system</c> (registered at boot in Program.cs) —
    /// the master DB itself is pure control-plane infrastructure (tenant
    /// registry + global Realm store) and is never a tenant. It is NOT a
    /// fallback: <c>TenantedSessionFactory</c> fails closed when no realm is
    /// resolved, and background work enters its realm explicitly with
    /// <c>TenantContext.Enter</c>.
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

    /// <summary>
    /// HttpContext.Items key under which the resolved Application id (ADR-0011)
    /// is stored by <c>RealmMiddleware</c> when the request arrived on an
    /// Application subdomain. Absent = no Application in context (a plain tenant
    /// host). The value is the owning <c>App.Id</c> (a <see cref="System.Guid"/>).
    /// </summary>
    public const string HttpContextApplicationIdKey = "ApplicationId";
}
