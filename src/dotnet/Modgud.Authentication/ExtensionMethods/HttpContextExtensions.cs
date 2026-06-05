using System.Security.Claims;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.ExtensionMethods;

public static class HttpContextExtensions
{
    public static Guid? GetUserId(this HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return claim is not null && Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// Resolves the <see cref="Realm"/> for the current request from the
    /// ambient tenant that <c>RealmMiddleware</c> stamped onto
    /// <c>HttpContext.Items["TenantId"]</c>. Returns <c>null</c> when no
    /// tenant was resolved (the middleware would normally have 404'd first) —
    /// callers building outbound links MUST treat a <c>null</c> realm as a
    /// hard failure, never substitute a fallback host.
    /// </summary>
    public static async Task<Realm?> ResolveCurrentRealmAsync(
        this HttpContext httpContext,
        IRealmProvisioningService realmSvc,
        CancellationToken ct = default)
    {
        var tenantId = httpContext.Items[TenantConstants.HttpContextTenantIdKey] as string;
        if (string.IsNullOrEmpty(tenantId)) return null;
        return await realmSvc.GetRealmBySlugAsync(tenantId, ct);
    }
}
