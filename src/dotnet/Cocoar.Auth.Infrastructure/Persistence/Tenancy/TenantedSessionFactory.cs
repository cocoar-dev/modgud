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
        return _httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string
               ?? TenantConstants.SystemTenantId;
    }
}
