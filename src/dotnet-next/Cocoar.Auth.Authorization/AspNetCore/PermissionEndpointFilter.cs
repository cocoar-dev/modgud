using System.Security.Claims;
using Cocoar.Auth.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Authorization.AspNetCore;

/// <summary>
/// Minimal-API endpoint filter that gates execution on the authenticated user
/// having the specified permission. Usage:
/// <code>
/// app.MapGet("/admin/users", ...).RequiresPermission("app:admin");
/// </code>
/// Returns <c>401</c> when the request is anonymous and <c>403</c> when the
/// caller is authenticated but lacks the permission.
/// </summary>
public class PermissionEndpointFilter(string permission) : IEndpointFilter
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
        if (!await permissionService.HasPermissionAsync(userId, permission))
            return Results.Forbid();

        return await next(context);
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Gates every endpoint in the route group on the given permission. Returns
    /// 401 for anonymous callers and 403 when the caller lacks the permission.
    /// </summary>
    public static RouteGroupBuilder RequiresPermission(this RouteGroupBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new PermissionEndpointFilter(permission));
        return builder;
    }

    /// <summary>Per-endpoint variant of <see cref="RequiresPermission(RouteGroupBuilder,string)"/>.</summary>
    public static RouteHandlerBuilder RequiresPermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new PermissionEndpointFilter(permission));
        return builder;
    }
}
