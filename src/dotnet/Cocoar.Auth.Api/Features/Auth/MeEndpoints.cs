using System.Security.Claims;
using BuildingBlocks.Helper;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Services;
using Cocoar.Auth.Domain.OAuth.Applications;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Api.Features.Auth;

/// <summary>
/// "/me" endpoints — caller-introspection used by the SPA admin UI (cookie
/// auth) and by Cocoar SaaS resource servers like TimeToDo (OAuth bearer
/// auth) to retrieve the authenticated user's app-scoped permissions,
/// groups, and roles in one request.
///
/// <para>This is the Phase-3 distribution API ("Stufe 2b" of the
/// Applications plan). Permissions are resolved live from the IDP database
/// — cached in the consumer for typically 30s — so revoking a role takes
/// effect within that window without waiting for the access token to
/// expire. The token itself stays slim (only identity-shaped claims +
/// group names via UserInfo from Stufe 2a).</para>
/// </summary>
public static class MeEndpoints
{
    public static WebApplication MapMeEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/v1/me")
            .WithTags("Me")
            .RequireAuthorization(new AuthorizationPolicyBuilder(
                    IdentityConstants.ApplicationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
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

                var appSlug = await ResolveAppSlugAsync(app, httpContext.User, session);
                if (appSlug is null)
                {
                    return Results.BadRequest(new
                    {
                        Error = "Me.AppRequired",
                        Message = "Could not determine app. Pass ?app=<slug>, or use a bearer token whose client is linked to an App.",
                    });
                }

                var permissions = await permissionService.GetUserPermissionsAsync(userId.Value, appSlug);
                var groups = await permissionService.GetUserGroupsAsync(userId.Value);
                var roles = await permissionService.GetUserRolesAsync(userId.Value, appSlug);

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
    /// Reads the user id from cookie auth (NameIdentifier) or bearer auth
    /// (sub). Both schemes write the user's Guid as the principal subject.
    /// </summary>
    private static Guid? ResolveUserId(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? user.FindFirst(Claims.Subject)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Resolves the app slug for the request:
    /// <list type="bullet">
    ///   <item>Explicit <c>?app=</c> query param wins (after we verify the
    ///         slug resolves to a real, non-deleted App).</item>
    ///   <item>Otherwise we look up the bearer-token's client. If the
    ///         client is linked to <b>exactly one</b> App, we use that
    ///         slug. If linked to several, we cannot disambiguate — the
    ///         caller MUST pass <c>?app=</c>.</item>
    /// </list>
    /// Returns <c>null</c> when no app can be determined.
    /// </summary>
    private static async Task<string?> ResolveAppSlugAsync(
        string? appFromQuery, ClaimsPrincipal user, IDocumentSession session)
    {
        if (!string.IsNullOrWhiteSpace(appFromQuery))
        {
            // Trust-but-verify: confirm the requested app exists. An unknown
            // slug returns no permissions (filter miss in PermissionService),
            // but we'd rather treat that as the caller's mistake explicitly.
            var byQuery = await session.Query<App>()
                .FirstOrDefaultAsync(a => a.Slug == appFromQuery && !a.IsDeleted);
            return byQuery?.Slug;
        }

        var clientId = user.FindFirst(Claims.ClientId)?.Value;
        if (string.IsNullOrEmpty(clientId)) return null;

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted);
        if (client is null || client.AppIds.Count == 0) return null;

        // Multi-app client without a query hint: ambiguous on purpose. The
        // caller has to say which app it wants permissions for.
        if (client.AppIds.Count > 1) return null;

        var derived = await session.LoadAsync<App>(client.AppIds[0]);
        return derived?.IsDeleted == false ? derived.Slug : null;
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
