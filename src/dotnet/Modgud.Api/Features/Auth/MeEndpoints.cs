using System.Security.Claims;
using BuildingBlocks.Helper;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// "/me" endpoints — caller-introspection for the Modgud admin SPA
/// (cookie-auth only). Returns the authenticated user's app-scoped
/// permissions, groups, and roles in one shot for the active browser
/// session.
///
/// <para>This is deliberately a Cookie-only path. Resource servers
/// (TimeToDo, Knowledge, …) querying with their bearer tokens use
/// <c>/api/v1/distribution/me-permissions</c> instead — that endpoint
/// also requires the calling RS to authenticate via X-Resource-Server-*
/// headers, derives the App from the RS, and is the single home for all
/// future server-to-server IAM lookups.</para>
/// </summary>
public static class MeEndpoints
{
    public static WebApplication MapMeEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/v1/me")
            .WithTags("Me")
            // Cookie-only: this endpoint is for the browser session of the
            // currently logged-in admin/user. Bearer-auth is rejected so
            // the semantic stays clean — server-to-server traffic goes to
            // /api/v1/distribution/*.
            .RequireAuthorization(new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser()
                .Build());

        group.MapGet("permissions", async (
                string? app,
                HttpContext httpContext,
                IPermissionService permissionService,
                IDocumentSession session) =>
            {
                var userId = ResolveUserId(httpContext.User);
                if (userId is null) return Results.Unauthorized();

                // Cookie-auth path: the admin SPA is interactive — passing
                // ?app= is required for any app other than modgud so
                // the operator is explicit about what they're looking at.
                // Default to modgud (the IDP's own admin surface) when
                // omitted, matching the SPA's default landing context.
                var appSlug = string.IsNullOrWhiteSpace(app) ? AppSlugs.Modgud : app;
                var verified = await session.Query<App>()
                    .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted);
                if (verified is null)
                {
                    return Results.BadRequest(new
                    {
                        Error = "Me.UnknownApp",
                        Message = $"App '{appSlug}' not found in this realm.",
                    });
                }

                var permissions = await permissionService.GetUserPermissionsAsync(userId.Value, appSlug);
                var roles = await permissionService.GetUserRolesAsync(userId.Value, appSlug);

                // Groups are app-scoped via BoundTo. Mirror the same gate
                // PermissionService applies to roles: a group only surfaces
                // for the requesting app when its BoundTo contains the
                // slug — or the wildcard "*". Keeps the response from
                // leaking org-only groups (HR distribution lists, other
                // apps' admin groups) into a resource server that has no
                // business knowing about them.
                var allGroups = await permissionService.GetUserGroupsAsync(userId.Value);
                var groups = allGroups
                    .Where(g => g.BoundTo.Contains(PermissionService.AllAppsWildcard)
                                || g.BoundTo.Contains(appSlug))
                    .ToList();

                // Short cache so a chatty resource server doesn't hammer the
                // IAM but a permission revoke still takes effect quickly.
                httpContext.Response.Headers.CacheControl = "private, max-age=30";

                return Results.Ok(new MePermissionsResponse(
                    UserId: new ShortGuid(userId.Value).ToString(),
                    AppSlug: appSlug,
                    Permissions: permissions.ToArray(),
                    Groups: groups.Select(g => new MeGroupRef(new ShortGuid(g.Id).ToString(), g.Name)).ToArray(),
                    Roles: roles.Select(r => new MeRoleRef(new ShortGuid(r.Id).ToString(), r.Name)).ToArray()));
            })
            .WithName("V1_Me_Permissions");

        return application;
    }

    /// <summary>
    /// Reads the user id from the cookie-auth principal (NameIdentifier).
    /// </summary>
    private static Guid? ResolveUserId(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

public sealed record MePermissionsResponse(
    string UserId,
    string AppSlug,
    string[] Permissions,
    MeGroupRef[] Groups,
    MeRoleRef[] Roles);

public sealed record MeGroupRef(string Id, string Name);
public sealed record MeRoleRef(string Id, string Name);
