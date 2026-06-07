using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Modgud.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Marten <see cref="ISessionFactory"/> that resolves the active tenant from
/// <see cref="HttpContext.Items"/> (populated by <c>RealmMiddleware</c>) and
/// opens a tenant-scoped session against that tenant's database.
///
/// <para>
/// When no <see cref="HttpContext"/> is available (background services, hosted
/// services, tests without a request scope), it falls back to the
/// <see cref="TenantConstants.SystemTenantId"/> tenant, which is registered
/// against its own <c>{master}_system</c> database (the master DB itself is
/// pure control-plane infrastructure and holds no tenant content). So
/// single-tenant boots and infrastructure jobs work out of the box.
/// </para>
///
/// <para>
/// Wired via <c>AddMarten(...).BuildSessionsWith&lt;TenantedSessionFactory&gt;()</c>
/// so every <c>IDocumentSession</c> / <c>IQuerySession</c> injection is
/// transparently tenant-scoped.
/// </para>
/// </summary>
public sealed class TenantedSessionFactory : ISessionFactory, ITenantSessionFactory
{
    private readonly IDocumentStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TenantedSessionFactory> _logger;

    public TenantedSessionFactory(
        IDocumentStore store,
        IHttpContextAccessor httpContextAccessor,
        ILogger<TenantedSessionFactory> logger)
    {
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public IQuerySession QuerySession() => OpenQuerySession();

    public IDocumentSession OpenSession() => _store.LightweightSession(ResolveTenantId(forWrite: true));

    public IQuerySession OpenQuerySession() => _store.QuerySession(ResolveTenantId(forWrite: false));

    private string ResolveTenantId(bool forWrite)
    {
        // Explicit signals first: an ambient AsyncLocal TenantContext, then the
        // HttpContext.Items value RealmMiddleware stamps at the request boundary.
        //
        // Why AsyncLocal-first: RealmMiddleware sets BOTH the AsyncLocal and
        // HttpContext.Items["TenantId"] to the same value, so the common request
        // path is unaffected. The ordering matters for the cross-tenant-from-CP
        // case (C15c realm provisioning + resend invite): the endpoint runs on
        // the CP host (HttpContext = "system") but needs to write into the
        // newly-provisioned tenant's DB. With HttpContext-first an explicit
        // TenantContext.Enter(tenantSlug) was silently ignored while HttpContext
        // still held the system value — the invite document landed in the wrong
        // DB and the magic-link resolver couldn't find it.
        var explicitTenant = TenantContext.CurrentOrNull
            ?? _httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string;
        if (explicitTenant is not null)
            return explicitTenant;

        // No tenant resolved. Two very different situations land here:
        //
        //   • No HttpContext at all → a genuine background / hosted-service /
        //     Wolverine-handler / CLI / test path. The system fallback is
        //     load-bearing there (single-tenant boots and infra jobs depend on
        //     it) and stays SILENT — this is by design.
        //
        //   • HttpContext present but no tenant → an in-flight HTTP request
        //     reached a tenant-scoped session without a resolved realm. This is
        //     the silent-fallback CLASS OF BUG ("I created it, got no error, and
        //     it isn't where I expected"): RealmMiddleware resolves (or 404s)
        //     every routed request, so this can only be a realm-agnostic
        //     skip-path (/health, /openapi, …) — a path that must NEVER perform
        //     a tenant-scoped write. Refuse writes loudly; warn on reads.
        var http = _httpContextAccessor.HttpContext;
        if (http is not null)
        {
            if (forWrite)
            {
                _logger.LogError(
                    "Refusing a tenant-scoped WRITE during HTTP request to {Path}: no realm was resolved "
                    + "(neither TenantContext nor HttpContext.Items[\"{Key}\"] carried a tenant). Falling back "
                    + "to the '{System}' tenant here would silently write to the wrong database. If this is a "
                    + "legitimate cross-tenant or background path, enter the tenant explicitly with "
                    + "TenantContext.Enter(...).",
                    http.Request.Path, TenantConstants.HttpContextTenantIdKey, TenantConstants.SystemTenantId);

                throw new InvalidOperationException(
                    $"No realm/tenant resolved for the current HTTP request ({http.Request.Path}); refusing to "
                    + $"open a tenant-scoped write session that would silently fall back to the "
                    + $"'{TenantConstants.SystemTenantId}' tenant. Enter the intended tenant explicitly with "
                    + "TenantContext.Enter(...) if this is a deliberate cross-tenant write.");
            }

            _logger.LogWarning(
                "Tenant-scoped READ during HTTP request to {Path} with no resolved realm — falling back to the "
                + "'{System}' tenant. Expected on realm-agnostic infra paths; unexpected on a routed endpoint.",
                http.Request.Path, TenantConstants.SystemTenantId);
        }

        return TenantConstants.SystemTenantId;
    }
}
