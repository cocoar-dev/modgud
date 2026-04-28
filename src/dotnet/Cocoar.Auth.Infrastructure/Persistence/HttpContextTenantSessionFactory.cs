using Marten;
using Microsoft.AspNetCore.Http;

namespace Cocoar.Auth.Infrastructure.Persistence;

/// <summary>
/// Creates tenant-scoped Marten sessions by resolving the tenant ID from HttpContext.
/// Falls back to "system" when no HttpContext is available (e.g., during startup seeding).
/// </summary>
public class HttpContextTenantSessionFactory : ITenantSessionFactory
{
	private readonly IDocumentStore _store;
	private readonly IHttpContextAccessor _httpContextAccessor;

	public HttpContextTenantSessionFactory(IDocumentStore store, IHttpContextAccessor httpContextAccessor)
	{
		_store = store;
		_httpContextAccessor = httpContextAccessor;
	}

	public IDocumentSession OpenSession()
	{
		var tenantId = ResolveTenantId();
		return _store.DirtyTrackedSession(tenantId);
	}

	public IQuerySession OpenQuerySession()
	{
		var tenantId = ResolveTenantId();
		return _store.QuerySession(tenantId);
	}

	private string ResolveTenantId()
	{
		return _httpContextAccessor.HttpContext?.Items["TenantId"] as string ?? "system";
	}
}
