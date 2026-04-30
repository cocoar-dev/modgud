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
/// Server-to-server distribution API — the home for resource servers
/// (TimeToDo, Knowledge, …) calling Cocoar.Auth on behalf of an
/// authenticated user. Every endpoint under <c>/api/v1/distribution/*</c>
/// requires <b>both</b> a user-bearer access token AND
/// resource-server credentials in the
/// <c>X-Resource-Server-Id</c> / <c>X-Resource-Server-Secret</c> headers.
///
/// <para>The App context is derived from the authenticated RS, so callers
/// don't pass <c>?app=</c> — the calling RS's <see cref="Domain.OAuth.Apis.OAuthApiState.AppId"/>
/// IS the request's app.</para>
///
/// <para>Browser / cookie-auth callers use <c>/api/v1/me/*</c> instead —
/// that path is intentionally Cookie-only and meant for the admin SPA's
/// self-introspection.</para>
/// </summary>
public static class DistributionEndpoints
{
    public static WebApplication MapDistributionEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/v1/distribution")
            .WithTags("Distribution")
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
