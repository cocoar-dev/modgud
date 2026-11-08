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
/// Realm-independent paths (<see cref="RealmIndependentPathPolicy"/>: health probes,
/// OpenAPI, installation, static assets) bypass realm resolution. Any other host
/// without a matching realm gets 404; in Development and Testing a localhost host
/// resolves to the only active realm (<c>RealmCacheLookup</c>).
/// There is no fallback to a system tenant.
/// </para>
/// </summary>
public sealed class RealmMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRealmCache _realmCache;

    public RealmMiddleware(RequestDelegate next, IRealmCache realmCache)
    {
        _next = next;
        _realmCache = realmCache;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Keep this classification identical to Program's terminal branch.
        // If realm resolution skips a path that the branch does not catch,
        // tenant-scoped session/DataProtection would execute without a realm.
        // /signalr deliberately does not match: it is realm-scoped so its auth
        // cookie can be decrypted with the correct tenant's keys.
        if (RealmIndependentPathPolicy.Matches(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var hostname = context.Request.Host.Host;
        var resolution = await _realmCache.ResolveAsync(hostname);

        if (resolution is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var tenantInfo = resolution.Tenant;
        context.Items[TenantConstants.HttpContextTenantIdKey] = tenantInfo.Slug;
        context.Items[TenantConstants.HttpContextTenantInfoKey] = tenantInfo;

        // ADR-0011 — when the request arrived on an Application subdomain, pin the
        // resolved Application so downstream (first-signal-consistency check,
        // settings cascade, branding) can read it. Absent on plain tenant hosts.
        if (resolution.ApplicationId is { } applicationId)
            context.Items[TenantConstants.HttpContextApplicationIdKey] = applicationId;

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
