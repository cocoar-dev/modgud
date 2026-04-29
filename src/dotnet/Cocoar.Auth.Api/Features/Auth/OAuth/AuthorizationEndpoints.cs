using System.Security.Claims;
using Cocoar.Auth.Authentication.Domain;
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
        UserManager<ApplicationUser> userManager)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

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
        UserManager<ApplicationUser> userManager)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

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
        UserManager<ApplicationUser> userManager)
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

        // TODO: Roles + custom claims via the new Cocoar.Auth.Authorization permission model.
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
    {
        if (!string.IsNullOrEmpty(user.Firstname) || !string.IsNullOrEmpty(user.Lastname))
        {
            return $"{user.Firstname} {user.Lastname}".Trim();
        }
        return user.UserName;
    }

    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (claim.Subject?.HasScope(Scopes.Profile) == true) yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (claim.Subject?.HasScope(Scopes.Email) == true) yield return Destinations.IdentityToken;
                yield break;

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
