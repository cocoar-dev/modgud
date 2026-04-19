using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using AbacPermissions = Cocoar.Auth.Application.Authorization.IPermissionService;

namespace Cocoar.Auth.Api.Authorization;

/// <summary>
/// Authorization filter for the new ABAC permission system.
/// Resolves permissions through transitive group membership via
/// <see cref="Cocoar.Auth.Application.Authorization.IPermissionService"/>.
/// <para>
/// Holders of <c>system:admin</c> or <c>tenant:admin</c> bypass every check
/// (handled inside the permission service).
/// </para>
/// <para>
/// Usage: <c>[RequiresAbacPermission("authorization-group:create")]</c>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequiresAbacPermissionAttribute(string permission) : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var abac = context.HttpContext.RequestServices.GetRequiredService<AbacPermissions>();
        if (!await abac.HasPermissionAsync(userId, permission, context.HttpContext.RequestAborted))
        {
            context.Result = new ForbidResult();
        }
    }
}
