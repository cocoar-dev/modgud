using System.Security.Claims;
using BuildingBlocks.Helper;
using Cocoar.Auth.Api.Features.Auth;
using Cocoar.Auth.Authorization.Services;
using Marten;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Api.Features.Distribution;

/// <summary>
/// Server-to-server distribution API — historically the home for resource
/// servers calling Cocoar.Auth on behalf of an authenticated user.
///
/// <para><b>Deprecated.</b> Per the finalised permission model
/// (<c>website/dev-notes/future-features/permission-modell.md</c> §7),
/// <c>/connect/userinfo</c> emits the same per-Audience
/// <c>resource_access</c> blocks (with bypass tiers pre-expanded), so
/// this endpoint has no remaining use case. Standard OIDC tooling reads
/// UserInfo directly; the Cocoar helper lib's claims-transformation
/// flattens the matching audience block onto the principal.</para>
///
/// <para>The endpoint stays operational so any external caller pinned to
/// the old shape continues to work — but every successful response
/// carries a <c>Deprecation: true</c> header per RFC 8594, plus a
/// <c>Sunset</c>-style note in the body, and the surface will be
/// removed in a follow-up commit once we're confident no one's still
/// pointing at it.</para>
/// </summary>
public static class DistributionEndpoints
{
    public static WebApplication MapDistributionEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/v1/distribution")
            .WithTags("Distribution (deprecated)")
            // Bearer-only: this surface is server-to-server. A cookie
            // session has no place here (the SPA goes through /me).
            .RequireAuthorization(new AuthorizationPolicyBuilder(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build())
            // RS-Auth is required, not optional. The filter rejects with
            // 401 when the X-Resource-Server-* headers are missing or
            // invalid — see RequireResourceServerAuthAsync below for the
            // additional "must be present" enforcement.
            .AddEndpointFilter<ResourceServerAuthFilter>();

        group.MapGet("me-permissions", async (
                HttpContext httpContext,
                IPermissionService permissionService) =>
            {
                var rs = httpContext.Items[ResourceServerAuthFilter.ContextItemKey] as ResourceServerContext;
                if (rs is null)
                {
                    httpContext.Response.Headers.WWWAuthenticate =
                        "CocoarAuthRS error=\"invalid_client\", error_description=\"Resource-server credentials are required on this endpoint.\"";
                    return Results.Unauthorized();
                }
                if (rs.App is null)
                {
                    return Results.BadRequest(new
                    {
                        Error = "Distribution.ResourceServerUnassigned",
                        Message = $"Resource server '{rs.ApiName}' is not linked to any App. Assign one in the OAuth API admin first.",
                    });
                }

                var sub = httpContext.User.FindFirst(Claims.Subject)?.Value
                          ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId))
                    return Results.Unauthorized();

                var appSlug = rs.App.Slug;
                var permissions = await permissionService.GetUserPermissionsAsync(userId, appSlug);
                var roles = await permissionService.GetUserRolesAsync(userId, appSlug);

                // Mirror the /me Groups filter: only groups whose BoundTo
                // contains the calling RS's App slug (or the wildcard).
                var allGroups = await permissionService.GetUserGroupsAsync(userId);
                var groups = allGroups
                    .Where(g => g.BoundTo.Contains(PermissionService.AllAppsWildcard)
                                || g.BoundTo.Contains(appSlug))
                    .ToList();

                // Short cache so a chatty resource server doesn't hammer the
                // IAM but a permission revoke still takes effect quickly.
                httpContext.Response.Headers.CacheControl = "private, max-age=30";

                // RFC 8594 deprecation signal — the response is still valid
                // and useful, but every consumer should migrate to /connect/userinfo
                // (which emits the same per-audience resource_access shape now).
                httpContext.Response.Headers["Deprecation"] = "true";
                httpContext.Response.Headers.Link =
                    "</connect/userinfo>; rel=\"successor-version\"; type=\"application/json\"";

                return Results.Ok(new MePermissionsResponse(
                    UserId: new ShortGuid(userId).ToString(),
                    AppSlug: appSlug,
                    Permissions: permissions.ToArray(),
                    Groups: groups.Select(g => new MeGroupRef(new ShortGuid(g.Id).ToString(), g.Name)).ToArray(),
                    Roles: roles.Select(r => new MeRoleRef(new ShortGuid(r.Id).ToString(), r.Name)).ToArray()));
            })
            .WithName("V1_Distribution_MePermissions");

        return application;
    }
}
