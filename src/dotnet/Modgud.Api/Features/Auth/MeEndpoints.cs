using System.Security.Claims;
using BuildingBlocks.Helper;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Persistence.Tenancy;
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
/// querying with their bearer tokens use
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

                // ADR-0011 — the App being introspected is resolved by the same
                // signal order as everywhere else: an explicit ?app= leads; absent
                // that, the Host-pinned App (when on an Application subdomain), else
                // modgud (the IdP's own admin surface, the SPA's default landing).
                var pinnedAppId = httpContext.GetApplicationId();
                App? verified;
                if (!string.IsNullOrWhiteSpace(app))
                    verified = await session.Query<App>().FirstOrDefaultAsync(a => a.Slug == app && !a.IsDeleted);
                else if (pinnedAppId is { } pinned)
                    verified = await session.Query<App>().FirstOrDefaultAsync(a => a.Id == pinned && !a.IsDeleted);
                else
                    verified = await session.Query<App>().FirstOrDefaultAsync(a => a.Slug == AppSlugs.Modgud && !a.IsDeleted);

                if (verified is null)
                {
                    return Results.BadRequest(new
                    {
                        Error = "Me.UnknownApp",
                        Message = $"App '{app ?? AppSlugs.Modgud}' not found in this realm.",
                    });
                }

                // ADR-0011 first-signal-consistency on the introspection path: if the
                // request arrived on an Application subdomain, the App being queried
                // must be that App. An explicit ?app= naming a different App is a
                // cross-app probe (e.g. acmelist.cocoar.app/me?app=portal) → reject.
                // On a plain tenant host (no Host pin) the operator may query any App.
                if (pinnedAppId is { } hostApp && verified.Id != hostApp)
                {
                    return Results.Json(
                        new
                        {
                            Error = "Me.AppMismatch",
                            Message = "The requested app does not match the application for this origin.",
                        },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var appSlug = verified.Slug;

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
