using Marten;
using Microsoft.AspNetCore.Http;

namespace Cocoar.Auth.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Marten <see cref="ISessionFactory"/> that resolves the active tenant from
/// <see cref="HttpContext.Items"/> (populated by <c>RealmMiddleware</c>) and
/// opens a tenant-scoped session against that tenant's database.
///
/// <para>
/// When no <see cref="HttpContext"/> is available (background services, hosted
/// services, tests without a request scope), it falls back to the
/// <see cref="TenantConstants.SystemTenantId"/> tenant. The system tenant
/// always points to the master DB so single-tenant boots and infrastructure
/// jobs work out of the box.
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

    public TenantedSessionFactory(IDocumentStore store, IHttpContextAccessor httpContextAccessor)
    {
        _store = store;
        _httpContextAccessor = httpContextAccessor;
    }

    public IQuerySession QuerySession() => OpenQuerySession();

    public IDocumentSession OpenSession() => _store.LightweightSession(ResolveTenantId());

    public IQuerySession OpenQuerySession() => _store.QuerySession(ResolveTenantId());

    private string ResolveTenantId()
    {
        // Two-layer resolution: HttpContext first (fastest path for the common
        // request-scoped case), then AsyncLocal TenantContext (covers inner DI
        // scopes and background paths that explicitly entered a tenant scope),
        // finally the system fallback. The AsyncLocal layer is what fixes
        // WOLV-01: a Wolverine handler that opened its own session via the
        // OutboxedSessionFactory used to land here without any tenant signal
        // and crash with "Default tenant does not supported".
        return _httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string
               ?? TenantContext.CurrentOrNull
               ?? TenantConstants.SystemTenantId;
    }
}
