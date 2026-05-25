using System.Security.Claims;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Authorization.AspNetCore;

/// <summary>
/// Minimal-API endpoint filter that gates execution on the authenticated user
/// having the specified permission within an app. Usage:
/// <code>
/// app.MapGet("/admin/users", ...).RequiresPermission("user:read");
/// app.MapPost("/admin/realms", ...).RequiresPermission("realm:write", AppSlugs.ControlPlane);
/// app.MapDelete("/me", ...).RequiresPermission("realm:admin");
/// </code>
/// <para>The <paramref name="permission"/> is the bare 2-segment
/// <c>"&lt;resource&gt;:&lt;action&gt;"</c> form except for the synthetic
/// <c>"realm:admin"</c> bypass which is recognised regardless of app context.
/// <see cref="PermissionEvaluator"/> handles both plus the resource-wide
/// admin bypass (<c>"&lt;resource&gt;:admin"</c>).</para>
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
    /// Gates every endpoint in the route group on the given permission within
    /// <see cref="AppSlugs.Modgud"/> by default. Pass
    /// <paramref name="appSlug"/> to gate against a different App's grants
    /// (e.g. <see cref="AppSlugs.ControlPlane"/> for cross-realm endpoints).
    /// External apps that authenticate via the distribution API run their
    /// own evaluation client-side — this filter is only for in-process gates.
    /// </summary>
    public static RouteGroupBuilder RequiresPermission(this RouteGroupBuilder builder, string permission, string? appSlug = null)
    {
        builder.AddEndpointFilter(new PermissionEndpointFilter(permission, appSlug ?? AppSlugs.Modgud));
        return builder;
    }

    /// <summary>Per-endpoint variant of <see cref="RequiresPermission(RouteGroupBuilder,string,string?)"/>.</summary>
    public static RouteHandlerBuilder RequiresPermission(this RouteHandlerBuilder builder, string permission, string? appSlug = null)
    {
        builder.AddEndpointFilter(new PermissionEndpointFilter(permission, appSlug ?? AppSlugs.Modgud));
        return builder;
    }
}
