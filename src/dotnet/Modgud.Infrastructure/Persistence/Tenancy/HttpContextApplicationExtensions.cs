using Microsoft.AspNetCore.Http;

namespace Modgud.Infrastructure.Persistence.Tenancy;

/// <summary>
/// ADR-0011 — convenience accessor for the Application pinned by
/// <c>RealmMiddleware</c> when a request arrives on an Application subdomain.
/// </summary>
public static class HttpContextApplicationExtensions
{
    /// <summary>
    /// The in-context Application id (the owning <c>App.Id</c>), or <c>null</c>
    /// when the request arrived on a plain tenant host (no Application pinned).
    /// </summary>
    public static Guid? GetApplicationId(this HttpContext context) =>
        context.Items.TryGetValue(TenantConstants.HttpContextApplicationIdKey, out var value)
        && value is Guid applicationId
            ? applicationId
            : null;
}
