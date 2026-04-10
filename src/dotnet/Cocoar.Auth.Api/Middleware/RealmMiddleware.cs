using Cocoar.Auth.Infrastructure.Services;

namespace Cocoar.Auth.Api.Middleware;

/// <summary>
/// Resolves the realm (tenant) from the HTTP Host header.
/// Domain-based routing: each tenant has one or more domains configured.
/// The middleware matches the Host header against the cached domain→tenant mapping.
/// </summary>
public class RealmMiddleware
{
	private readonly RequestDelegate _next;
	private readonly IRealmCache _realmCache;

	private static readonly string[] SkipPaths = ["/health", "/swagger", "/_framework"];

	public RealmMiddleware(RequestDelegate next, IRealmCache realmCache)
	{
		_next = next;
		_realmCache = realmCache;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		var path = context.Request.Path.Value;

		// Skip system paths that don't need tenant resolution
		if (path is not null && SkipPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
		{
			await _next(context);
			return;
		}

		// Resolve tenant from Host header
		var hostname = context.Request.Host.Host;
		var tenantInfo = await _realmCache.ResolveDomainAsync(hostname);

		if (tenantInfo is null)
		{
			context.Response.StatusCode = 404;
			return;
		}

		context.Items["TenantId"] = tenantInfo.Slug;
		context.Items["RealmSlug"] = tenantInfo.Slug;
		context.Items["TenantInfo"] = tenantInfo;

		await _next(context);
	}
}
