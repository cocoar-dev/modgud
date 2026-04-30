using System.Security.Claims;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Services;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Scopes;
using Marten;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Api.Features.Auth.OAuth;

/// <summary>
/// Minimal-API endpoints for the OpenIddict server: authorize, token, userinfo,
/// logout. This is the porting baseline for stage 3b — it carries enough to
/// satisfy the OpenIddict pipeline (discovery + JWKS work, sign-in/sign-out flow
/// compiles end-to-end), but role + custom-claim injection is intentionally
/// deferred (legacy <c>IRoleRepository</c> / <c>IEffectiveRolesService</c> don't
/// exist in the new authorization model — that bridging is a follow-up phase).
/// </summary>
public static class AuthorizationEndpoints
{
    public static WebApplication MapAuthorizationEndpoints(this WebApplication app, string pathBase = "connect")
    {
        var group = app.MapGroup($"~/{pathBase}").WithTags("OpenIddict");

        // Authorization endpoint — entry point of the auth-code flow.
        group.MapMethods("authorize", new[] { "GET", "POST" }, AuthorizeAsync)
            .WithName("OAuth_Authorize")
            .DisableAntiforgery();

        // Token endpoint — code/refresh/client_credentials/device exchange.
        group.MapPost("token", ExchangeAsync)
            .WithName("OAuth_Token")
            .DisableAntiforgery();

        // UserInfo endpoint — claims for the authenticated subject.
        group.MapMethods("userinfo", new[] { "GET", "POST" }, UserinfoAsync)
            .WithName("OAuth_UserInfo")
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
            });

        // Logout / end-session endpoint.
        group.MapMethods("logout", new[] { "GET", "POST" }, LogoutAsync)
            .WithName("OAuth_Logout");

        return app;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<ApplicationUser> userManager,
        IDocumentSession session)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Stufe-3 scope restriction: an app-scoped scope (Scope.AppId != null)
        // can only be requested by a client linked to the same App. Standard
        // OIDC scopes (openid/email/profile/roles/offline_access) are seeded
        // with AppId=null and therefore always pass.
        var scopeError = await ValidateScopeRestrictionAsync(
            request.ClientId, request.GetScopes(), session);
        if (scopeError is not null) return scopeError;

        var authResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var needsLogin = !authResult.Succeeded;

        if (authResult.Succeeded && request.MaxAge != null && authResult.Properties?.IssuedUtc != null)
        {
            var sessionAge = DateTimeOffset.UtcNow - authResult.Properties.IssuedUtc.Value;
            if (sessionAge > TimeSpan.FromSeconds(request.MaxAge.Value)) needsLogin = true;
        }

        if (!string.IsNullOrEmpty(request.Prompt) && request.Prompt.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            needsLogin = true;
        }

        if (needsLogin)
        {
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString },
                new[] { IdentityConstants.ApplicationScheme });
        }

        var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("Details concerning the calling client application cannot be found.");

        var user = await userManager.GetUserAsync(authResult.Principal!);
        if (user is null)
        {
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString },
                new[] { IdentityConstants.ApplicationScheme });
        }

        if (!user.IsActive || user.IsDeleted)
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user account has been deactivated.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        var subject = user.Id.ToString();
        var clientPk = await applicationManager.GetIdAsync(application) ?? string.Empty;

        var authorizations = await authorizationManager.FindAsync(
            subject: subject,
            client: clientPk,
            status: Statuses.Valid,
            type: AuthorizationTypes.Permanent,
            scopes: request.GetScopes()).ToListAsync();

        var consentType = await applicationManager.GetConsentTypeAsync(application);

        if (consentType == ConsentTypes.Implicit || authorizations.Count != 0)
        {
            var principal = await CreateClaimsPrincipalAsync(user, request, scopeManager);

            var authorization = authorizations.LastOrDefault();
            authorization ??= await authorizationManager.CreateAsync(
                principal: principal,
                subject: subject,
                client: clientPk,
                type: AuthorizationTypes.Permanent,
                scopes: principal.GetScopes());

            principal.SetAuthorizationId(await authorizationManager.GetIdAsync(authorization));

            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (!string.IsNullOrEmpty(request.Prompt) && request.Prompt.Contains("none", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.ConsentRequired,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Interactive user consent is required.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        // Explicit consent flow — bounce to the SPA consent page; it will POST
        // to /connect/consent and then re-issue the authorize request.
        var authorizeUrl = httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString;
        var consentUrl = $"/consent?returnUrl={Uri.EscapeDataString(authorizeUrl)}";
        return Results.Redirect(consentUrl);
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IDocumentSession session)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Stufe-3 scope restriction. Code/refresh-grant scopes were already
        // validated at /connect/authorize time, but defence in depth — and
        // client_credentials skips the authorize step entirely so we MUST
        // validate here too.
        var scopeError = await ValidateScopeRestrictionAsync(
            request.ClientId, request.GetScopes(), session);
        if (scopeError is not null) return scopeError;

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType() || request.IsDeviceCodeGrantType())
        {
            var result = await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var subject = result.Principal?.GetClaim(Claims.Subject);
            if (string.IsNullOrEmpty(subject)) return ForbidInvalidGrant("The token is no longer valid.");

            var user = await userManager.FindByIdAsync(subject);
            if (user is null) return ForbidInvalidGrant("The token is no longer valid.");

            if (!await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
            {
                return ForbidInvalidGrant("The user is no longer allowed to sign in.");
            }

            var originalScopes = result.Principal?.GetScopes();
            var principal = await CreateClaimsPrincipalAsync(user, request, scopeManager, originalScopes);
            principal.SetAuthorizationId(result.Principal?.GetAuthorizationId());

            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
                ?? throw new InvalidOperationException("The application cannot be found.");

            var identity = new ClaimsIdentity(
                authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                nameType: Claims.Name,
                roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, await applicationManager.GetClientIdAsync(application));
            identity.SetClaim(Claims.Name, await applicationManager.GetDisplayNameAsync(application));
            identity.SetScopes(request.GetScopes());

            var clientResources = await scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync();
            var clientId = await applicationManager.GetClientIdAsync(application);
            if (!string.IsNullOrEmpty(clientId) && !clientResources.Contains(clientId))
            {
                clientResources.Add(clientId);
            }
            identity.SetResources(clientResources);

            identity.SetDestinations(static claim => claim.Type switch
            {
                Claims.Name or Claims.Subject => new[] { Destinations.AccessToken, Destinations.IdentityToken },
                _ => new[] { Destinations.AccessToken },
            });

            return Results.SignIn(new ClaimsPrincipal(identity), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");

        static IResult ForbidInvalidGrant(string description) =>
            Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    private static async Task<IResult> UserinfoAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        IPermissionService permissionService,
        IDocumentSession session)
    {
        var subject = httpContext.User.GetClaim(Claims.Subject);
        if (string.IsNullOrEmpty(subject))
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is invalid.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        var user = await userManager.FindByIdAsync(subject);
        if (user is null)
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is bound to an account that no longer exists.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = user.Id.ToString(),
        };

        if (httpContext.User.HasScope(Scopes.Email) && !string.IsNullOrEmpty(user.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (httpContext.User.HasScope(Scopes.Profile))
        {
            claims[Claims.PreferredUsername] = user.UserName;
            claims[Claims.Name] = GetDisplayName(user);

            if (!string.IsNullOrEmpty(user.Firstname)) claims[Claims.GivenName] = user.Firstname;
            if (!string.IsNullOrEmpty(user.Lastname)) claims[Claims.FamilyName] = user.Lastname;
        }

        // Roles + groups: when the OIDC `roles` scope was granted, expose
        // both as claims with their proper semantics:
        //
        //   `role`   — names of PermissionRoles the user effectively holds
        //              in the calling client's App. This is the
        //              "what may you do" axis, which is what
        //              [Authorize(Roles="…")] actually asks. ASP.NET Core
        //              maps `role` → ClaimTypes.Role automatically when the
        //              resource server has GetClaimsFromUserInfoEndpoint=true.
        //   `groups` — names of Groups the user is a member of. This is the
        //              organisational/distribution-list axis (e.g. "send
        //              mail to all HR-Gruppe members"), not authorisation.
        //
        // The role list is App-scoped: the App is derived from the access
        // token's client_id → OAuthApplication.AppId → App.Slug (the
        // Stufe-1 link). Falls back to cocoar-auth when the calling client
        // is realm-wide / unassigned, so the IDP's own admin SPA still
        // sees its own roles.
        //
        // Granular permissions are intentionally NOT in UserInfo — they
        // refresh per-request via the distribution API
        // (GET /api/v1/me/permissions?app=…). UserInfo carries only
        // identity-shaped claims that change rarely.
        if (httpContext.User.HasScope(Scopes.Roles))
        {
            var appSlug = await ResolveAppSlugFromClientAsync(httpContext.User, session)
                          ?? AppSlugs.CocoarAuth;

            var roles = await permissionService.GetUserRolesAsync(user.Id, appSlug);
            var groups = await permissionService.GetUserGroupsAsync(user.Id);

            if (roles.Count > 0)
                claims[Claims.Role] = roles.Select(r => r.Name).ToArray();
            if (groups.Count > 0)
                claims["groups"] = groups.Select(g => g.Name).ToArray();
        }

        return Results.Ok(claims);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        SignInManager<ApplicationUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    private static async Task<ClaimsPrincipal> CreateClaimsPrincipalAsync(
        ApplicationUser user,
        OpenIddictRequest request,
        IOpenIddictScopeManager scopeManager,
        IEnumerable<string>? scopeOverrides = null)
    {
        // Identity must use the OpenIddict default authentication type so it processes
        // the claims correctly (Identity's ApplicationScheme identity is filtered out).
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString());

        var principal = new ClaimsPrincipal(identity);
        var scopes = scopeOverrides ?? request.GetScopes();
        principal.SetScopes(scopes);

        var resources = await scopeManager.ListResourcesAsync(principal.GetScopes()).ToListAsync();
        if (!string.IsNullOrEmpty(request.ClientId) && !resources.Contains(request.ClientId))
        {
            resources.Add(request.ClientId);
        }
        principal.SetResources(resources);

        if (principal.HasScope(Scopes.Email) && !string.IsNullOrEmpty(user.Email))
        {
            identity.SetClaim(Claims.Email, user.Email);
            identity.SetClaim(Claims.EmailVerified, user.EmailConfirmed.ToString().ToLowerInvariant());
        }

        if (principal.HasScope(Scopes.Profile))
        {
            identity.SetClaim(Claims.PreferredUsername, user.UserName);
            identity.SetClaim(Claims.Name, GetDisplayName(user));

            if (!string.IsNullOrEmpty(user.Firstname)) identity.SetClaim(Claims.GivenName, user.Firstname);
            if (!string.IsNullOrEmpty(user.Lastname)) identity.SetClaim(Claims.FamilyName, user.Lastname);
        }

        // TODO: Inject role/permission claims via the Cocoar.Auth.Authorization
        // principal directory once the bridge to OpenIddict is implemented.

        principal.SetDestinations(GetDestinations);
        return principal;
    }

    private static string GetDisplayName(ApplicationUser user)
        => AuthorizationEndpointHelpers.GetDisplayName(user);

    private static IEnumerable<string> GetDestinations(Claim claim)
        => AuthorizationEndpointHelpers.GetDestinations(claim);

    /// <summary>
    /// Resolves the app slug from the bearer token's <c>client_id</c> claim:
    /// looks up the OAuth client, follows its <c>AppId</c> to <see cref="App"/>
    /// and returns the slug. Returns <c>null</c> when no client_id is on the
    /// principal (interactive cookie auth), the client is not found, the
    /// client has no App link, or the App was deleted. Used by UserInfo to
    /// scope roles to the calling App.
    /// </summary>
    private static async Task<string?> ResolveAppSlugFromClientAsync(
        ClaimsPrincipal user, IDocumentSession session)
    {
        var clientId = user.FindFirst(Claims.ClientId)?.Value;
        if (string.IsNullOrEmpty(clientId)) return null;

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted);
        if (client?.AppId is not Guid appId) return null;

        var app = await session.LoadAsync<App>(appId);
        return app?.IsDeleted == false ? app.Slug : null;
    }

    /// <summary>
    /// Stufe-3 scope restriction. Returns <c>null</c> if every requested scope
    /// is allowed for the calling client; otherwise returns a forbid result
    /// carrying <c>invalid_scope</c>.
    ///
    /// <para>Rules:</para>
    /// <list type="bullet">
    ///   <item>Scope not registered in our DB → ignored here (let OpenIddict
    ///         decide; e.g. built-in <c>openid</c> on a fresh tenant).</item>
    ///   <item>Scope registered with <c>AppId == null</c> → always allowed
    ///         (standard OIDC scopes, cross-app utility scopes).</item>
    ///   <item>Scope registered with a non-null <c>AppId</c> → only allowed
    ///         when the calling client's <c>AppId</c> matches.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult?> ValidateScopeRestrictionAsync(
        string? clientId, IEnumerable<string> requestedScopes, IDocumentSession session)
    {
        var scopeNames = requestedScopes?.ToArray() ?? Array.Empty<string>();
        if (scopeNames.Length == 0) return null;

        // Load scope projections by name once — far fewer DB hits than per-scope.
        var scopes = await session.Query<OAuthScopeState>()
            .Where(s => scopeNames.Contains(s.Name) && !s.IsDeleted)
            .ToListAsync();

        // If no requested scope is app-scoped, there's nothing to restrict.
        var appScoped = scopes.Where(s => s.AppId.HasValue).ToList();
        if (appScoped.Count == 0) return null;

        // App-scoped scopes are present — we must know the client's app.
        Guid? clientAppId = null;
        if (!string.IsNullOrEmpty(clientId))
        {
            var client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted);
            clientAppId = client?.AppId;
        }

        var bad = appScoped.FirstOrDefault(s => s.AppId != clientAppId);
        if (bad is not null)
        {
            var description = clientAppId is null
                ? $"Scope '{bad.Name}' is restricted to a specific app, but the calling client is not linked to one."
                : $"Scope '{bad.Name}' belongs to a different app than the calling client.";
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidScope,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        return null;
    }
}

/// <summary>
/// Pure helpers extracted from <see cref="AuthorizationEndpoints"/> so the
/// id-token claim destinations (which scopes leak which claim into the
/// id_token) and the userinfo display-name fallback can be unit-tested
/// without a host. Both are subtle correctness contracts toward OpenIddict
/// — drift here changes what gets baked into tokens.
/// <para>Internal — only the endpoint and its tests should depend on it.</para>
/// </summary>
internal static class AuthorizationEndpointHelpers
{
    /// <summary>
    /// Returns "Firstname Lastname" if either is present (trimmed for the
    /// firstname-only / lastname-only edge cases), else the username.
    /// Used both in the userinfo response and as the <c>name</c> claim source
    /// when the <c>profile</c> scope is granted.
    /// </summary>
    public static string GetDisplayName(ApplicationUser user)
    {
        if (!string.IsNullOrEmpty(user.Firstname) || !string.IsNullOrEmpty(user.Lastname))
        {
            return $"{user.Firstname} {user.Lastname}".Trim();
        }
        return user.UserName;
    }

    /// <summary>
    /// Maps each claim type to the token(s) it may be embedded in. The
    /// per-scope guard ensures we never put profile data in the id_token
    /// unless the relying party actually asked for the matching scope.
    /// <c>SecurityStamp</c> is intentionally yielded into NEITHER token — it's
    /// internal to ASP.NET Identity and would be a leak.
    /// <para>
    /// All standard OIDC claims that the principal-builder actually sets
    /// (<see cref="AuthorizationEndpoints.CreateClaimsPrincipalAsync"/>) must
    /// have an explicit case here — otherwise they fall through to the
    /// access-token-only default and never reach the id_token. The default is
    /// the conservative choice for unknown custom app claims.
    /// </para>
    /// </summary>
    public static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            // OIDC `profile` scope claims
            case Claims.Name or Claims.PreferredUsername or Claims.GivenName or Claims.FamilyName:
                yield return Destinations.AccessToken;
                if (claim.Subject?.HasScope(Scopes.Profile) == true) yield return Destinations.IdentityToken;
                yield break;

            // OIDC `email` scope claims
            case Claims.Email or Claims.EmailVerified:
                yield return Destinations.AccessToken;
                if (claim.Subject?.HasScope(Scopes.Email) == true) yield return Destinations.IdentityToken;
                yield break;

            // OIDC `roles` scope claim
            case Claims.Role:
                yield return Destinations.AccessToken;
                if (claim.Subject?.HasScope(Scopes.Roles) == true) yield return Destinations.IdentityToken;
                yield break;

            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
