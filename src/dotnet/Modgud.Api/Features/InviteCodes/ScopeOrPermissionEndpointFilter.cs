using System.Security.Claims;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Applications;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.InviteCodes;

/// <summary>
/// Dual-auth endpoint filter for the app-scoped invite-code surface (ADR-0012 §7,
/// D9). A request passes if EITHER:
/// <list type="bullet">
///   <item><b>M2M</b>: the validated access token carries the app-bound
///   <c>invite:*</c> OAuth scope AND the token's client is bound to the
///   <c>{appId}</c> in the route (the ADR-0011 first-signal-consistency invariant
///   applied to minting — a cross-app or cross-tenant caller is rejected, never
///   coerced). The scope being app-bound means OpenIddict's
///   <c>ValidateScopeRestriction</c> already refused the token at issuance unless
///   the client was authorized for that app; the AppIds check here ties the
///   targeted <c>{appId}</c> to that same client.</item>
///   <item><b>Admin</b>: the cookie-authenticated user holds the in-process
///   <c>invite-code:*</c> permission (the permission equivalent, ADR-0005).</item>
/// </list>
/// Returns <c>401</c> when anonymous, <c>403</c> (naming both accepted grants)
/// otherwise. The endpoint group must enable BOTH the cookie and the OpenIddict
/// validation scheme so <c>HttpContext.User</c> is populated for either caller.
/// </summary>
public sealed class ScopeOrPermissionEndpointFilter(string scope, string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var user = httpContext.User;

        if (user.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        if (!TryGetRouteAppId(httpContext, out var appId))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid appId", detail: "The {appId} route value is not a valid id.");

        // ── M2M path: app-bound OAuth scope + client bound to {appId} ──
        if (user.HasScope(scope))
        {
            var clientId = user.GetClaim(Claims.ClientId) ?? user.GetClaim(Claims.AuthorizedParty);
            if (!string.IsNullOrEmpty(clientId))
            {
                var session = httpContext.RequestServices.GetRequiredService<IDocumentSession>();
                var client = await session.Query<OAuthApplicationState>()
                    .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, httpContext.RequestAborted);
                if (client is not null && client.AppIds.Contains(appId))
                    return await next(context);
            }

            // Has the scope but the token's client isn't bound to this app →
            // cross-app minting attempt. Reject (don't fall through to permission;
            // a M2M caller has no cookie user anyway).
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: $"The token's client is not authorized for app '{new ShortGuid(appId)}'.");
        }

        // ── Admin path: in-process permission on the cookie user ──
        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is not null && Guid.TryParse(userIdClaim, out var userId))
        {
            var permissionService = httpContext.RequestServices.GetRequiredService<IPermissionService>();
            if (await permissionService.HasPermissionAsync(userId, AppSlugs.Modgud, permission))
                return await next(context);
        }

        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: $"Requires the '{scope}' OAuth scope (M2M) or the '{permission}' permission (admin).");
    }

    private static bool TryGetRouteAppId(HttpContext httpContext, out Guid appId)
    {
        appId = Guid.Empty;
        var raw = httpContext.Request.RouteValues.TryGetValue("appId", out var v) ? v?.ToString() : null;
        return raw is not null && ShortGuid.TryParse(raw, out appId);
    }
}
