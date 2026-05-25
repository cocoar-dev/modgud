using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Middleware;

/// <summary>
/// Enforces Control-Plane / Data-Plane separation at the routing layer (C14).
/// Cross-realm administration paths (realm CRUD + the first-run setup wizard)
/// are 404'd when the resolved tenant is not the Control-Plane realm.
///
/// <para>404 — not 401/403 — is deliberate: tenant realms shouldn't even be
/// able to discover that a global-admin surface exists at this hostname. A
/// portscan of <c>tenant-a.example.com</c> looks identical to a server that
/// never had those endpoints.</para>
///
/// <para>This is the routing-layer half of the C14 pair. The other half is
/// <see cref="Modgud.Api.Features.Admin.RequireControlPlaneFilter"/>
/// (per-endpoint filter on /api/admin/realms/*). Either alone would close
/// the gap; both together mean a misconfigured hostname list still can't
/// expose realm-management to a tenant realm — and a missing endpoint
/// filter is still caught by the routing gate.</para>
///
/// <para>Order: this middleware MUST run after <see cref="RealmMiddleware"/>
/// so <see cref="HttpContext.Items"/>[<see cref="TenantConstants.HttpContextTenantInfoKey"/>]
/// is populated. It deliberately runs before <c>UseAuthentication</c> so
/// even cookie validation never touches a route that isn't supposed to
/// exist on this host.</para>
/// </summary>
public sealed class ControlPlaneGateMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Path prefixes that are restricted to the Control-Plane realm.
    /// Match is case-insensitive prefix on <see cref="HttpRequest.Path"/>.
    /// </summary>
    private static readonly string[] ControlPlaneOnlyPaths =
    [
        "/api/admin/realms",
        // /api/setup was removed in C15d — there is no first-run wizard
        // anymore. Bootstrap-invite consumption (POST /api/account/bootstrap-admin)
        // runs on the tenant host on purpose, so it is NOT listed here.
    ];

    public ControlPlaneGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !IsControlPlaneOnlyPath(path))
        {
            await _next(context);
            return;
        }

        var tenant = context.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
        if (tenant is null || !tenant.IsControlPlane)
        {
            // Hide the existence of the endpoint from non-CP tenants.
            // Logging at Debug so a misconfigured deployment leaves a trail
            // without polluting prod logs on every probe.
            Serilog.Log.Debug(
                "ControlPlaneGate: 404 — path '{Path}' is Control-Plane-only, tenant '{Slug}' is not Control Plane",
                path, tenant?.Slug ?? "<none>");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }

    internal static bool IsControlPlaneOnlyPath(string path)
    {
        foreach (var prefix in ControlPlaneOnlyPaths)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
