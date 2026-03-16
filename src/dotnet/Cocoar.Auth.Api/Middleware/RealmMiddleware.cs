using Cocoar.Auth.Infrastructure.Services;

namespace Cocoar.Auth.Api.Middleware;

/// <summary>
/// Resolves the realm (tenant) from the URL path.
/// URLs with /realms/{slug}/... set TenantId to the slug and rewrite PathBase.
/// URLs without /realms/ prefix route to the system realm (backward compatibility).
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

		if (path is not null && path.StartsWith("/realms/", StringComparison.OrdinalIgnoreCase))
		{
			// Extract slug from /realms/{slug}/...
			var afterRealms = path["/realms/".Length..];
			var slashIdx = afterRealms.IndexOf('/');
			var slug = slashIdx >= 0 ? afterRealms[..slashIdx] : afterRealms;
			var remainingPath = slashIdx >= 0 ? afterRealms[slashIdx..] : "/";

			// Validate realm exists and is active
			if (string.IsNullOrEmpty(slug) || !await _realmCache.IsValidRealmAsync(slug))
			{
				context.Response.StatusCode = 404;
				return;
			}

			context.Items["TenantId"] = slug;
			context.Items["RealmSlug"] = slug;
			context.Request.PathBase = new PathString($"/realms/{slug}");
			context.Request.Path = new PathString(remainingPath);
		}
		else
		{
			// No realm prefix → system realm (backward compat)
			context.Items["TenantId"] = "system";
			context.Items["RealmSlug"] = "system";
		}

		await _next(context);
	}
}
