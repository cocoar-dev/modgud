using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Cocoar.Auth.Infrastructure.Realms;

/// <summary>
/// Endpoint filter that returns 404 when the request's resolved realm is not
/// the Control Plane (<c>Realm.IsControlPlane</c>). 404 (not 403) is
/// deliberate: tenant realms shouldn't even know that the global admin
/// surface exists at this hostname.
///
/// <para>Lives in Infrastructure (not Api) so auth-slice endpoints can
/// pull the same filter without an Api → Auth circular reference.
/// Currently used by <c>RealmsEndpoints</c> in <c>Cocoar.Auth.Api</c> —
/// the realm CRUD admin surface. (The C15d cleanup removed the previous
/// SetupEndpoints user; the filter is kept available for future
/// CP-only routes.)</para>
///
/// <para>This is the in-app belt-and-suspenders complement to
/// <c>ControlPlaneGateMiddleware</c>, which short-circuits earlier in the
/// pipeline based on the configured Control-Plane hostname list. Either
/// alone would be sufficient; both together mean a misconfigured hostname
/// list still can't expose a Control-Plane endpoint to a tenant realm.</para>
/// </summary>
public sealed class RequireControlPlaneFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Fail-closed: any of (no TenantInfo populated, realm exists but
        // isn't the Control Plane) → 404. Hides the existence of the
        // endpoint from non-CP hosts. Filter stays Serilog-free so it
        // can live in Infrastructure without taking that dependency;
        // the routing-layer ControlPlaneGateMiddleware (in Cocoar.Auth.Api)
        // already emits a Debug-level trail for the same cases.
        var info = context.HttpContext.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
        if (info is null || !info.IsControlPlane)
            return ValueTask.FromResult<object?>(Results.NotFound());

        return next(context);
    }
}
