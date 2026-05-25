using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Middleware;

/// <summary>
/// Resolves the realm (tenant) from the HTTP Host header. Domain-based routing:
/// each realm has one or more domains configured (see <see cref="Domain.Realms.Realm.Domains"/>).
/// The middleware matches the Host header against the cached domain → tenant
/// mapping and stashes the resolved tenant id into <see cref="HttpContext.Items"/>
/// so the <c>TenantedSessionFactory</c> can pick it up when opening Marten
/// sessions inside the request scope.
///
/// <para>
/// Skip-paths bypass realm resolution (used by health probes, OpenAPI, etc.).
/// Anything else without a matching realm falls through to the system tenant
/// when only one realm exists (single-tenant boot) or returns 404 when the host
/// is genuinely unknown.
/// </para>
/// </summary>
public sealed class RealmMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRealmCache _realmCache;

    private static readonly string[] SkipPaths =
    [
        "/health",
        "/swagger",
        "/openapi",
        "/_framework",
        "/signalr",
    ];

    public RealmMiddleware(RequestDelegate next, IRealmCache realmCache)
    {
        _next = next;
        _realmCache = realmCache;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;

        if (path is not null && SkipPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var hostname = context.Request.Host.Host;
        var tenantInfo = await _realmCache.ResolveDomainAsync(hostname);

        if (tenantInfo is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Items[TenantConstants.HttpContextTenantIdKey] = tenantInfo.Slug;
        context.Items[TenantConstants.HttpContextTenantInfoKey] = tenantInfo;

        // Ambient AsyncLocal — survives DI-scope boundaries that
        // HttpContextAccessor alone doesn't. TenantedSessionFactory and the
        // OpenIddict per-realm signing handlers consult this when no
        // HttpContext is reachable (background services, inner-scope buses,
        // Wolverine handlers). Restored automatically when the request scope
        // unwinds.
        using var _ = TenantContext.Enter(tenantInfo.Slug);

        await _next(context);
    }
}
