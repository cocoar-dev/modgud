using System.Security.Claims;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Authorization.AspNetCore;

/// <summary>
/// Minimal-API endpoint filter that gates execution on the authenticated user
/// having the specified permission within an app. Usage:
/// <code>
/// app.MapGet("/admin/users", ...).RequiresPermission("cocoar-auth:user:read");
/// app.MapDelete("/me", ...).RequiresPermission("realm:admin");
/// </code>
/// The <paramref name="permission"/> is fully qualified
/// (<c>app:resource:action</c>); <see cref="PermissionEvaluator"/> recognises
/// the standard bypasses (<c>realm:admin</c>, <c>app:admin</c>,
/// <c>app:resource:admin</c>).
///
/// <para>Returns <c>401</c> when the request is anonymous and <c>403</c> when
/// the caller is authenticated but lacks the permission.</para>
/// </summary>
public class PermissionEndpointFilter(string permission, string appSlug) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var user = httpContext.User;

        if (user.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            return Results.Unauthorized();

        var permissionService = httpContext.RequestServices.GetRequiredService<IPermissionService>();
        if (!await permissionService.HasPermissionAsync(userId, appSlug, permission))
            return Results.Forbid();

        return await next(context);
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Gates every endpoint in the route group on the given permission. The
    /// permission is evaluated within <see cref="AppSlugs.CocoarAuth"/> — the
    /// IDP itself. External apps will use a different code path (Phase 2
    /// distribution API) that carries their own slug.
    /// </summary>
    public static RouteGroupBuilder RequiresPermission(this RouteGroupBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new PermissionEndpointFilter(permission, AppSlugs.CocoarAuth));
        return builder;
    }

    /// <summary>Per-endpoint variant of <see cref="RequiresPermission(RouteGroupBuilder,string)"/>.</summary>
    public static RouteHandlerBuilder RequiresPermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new PermissionEndpointFilter(permission, AppSlugs.CocoarAuth));
        return builder;
    }
}
