using Cocoar.Auth.Infrastructure.Services;

namespace Cocoar.Auth.Api.Middleware;

/// <summary>
/// Resolves the realm (tenant) from the URL path.
/// The first path segment is always the realm slug (e.g. /system/api/..., /acme/api/...).
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

		// Skip system paths
		if (path is not null && SkipPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
		{
			await _next(context);
			return;
		}

		// Redirect root to system realm
		if (string.IsNullOrEmpty(path) || path == "/")
		{
			context.Response.StatusCode = 302;
			context.Response.Headers.Location = "/system/";
			return;
		}

		// Extract first path segment as realm slug
		var trimmed = path.AsSpan(1); // skip leading '/'
		var slashIdx = trimmed.IndexOf('/');
		var slug = (slashIdx >= 0 ? trimmed[..slashIdx] : trimmed).ToString();
		var remainingPath = slashIdx >= 0 ? trimmed[slashIdx..].ToString() : "/";

		// Validate realm exists and is active
		if (string.IsNullOrEmpty(slug) || !await _realmCache.IsValidRealmAsync(slug))
		{
			context.Response.StatusCode = 404;
			return;
		}

		context.Items["TenantId"] = slug;
		context.Items["RealmSlug"] = slug;
		context.Request.PathBase = new PathString($"/{slug}");
		context.Request.Path = new PathString(remainingPath);

		await _next(context);
	}
}
