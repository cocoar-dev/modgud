using System.Security.Claims;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Apis;
using Modgud.Permissions;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Consent;
using Modgud.Domain.OAuth.Scopes;
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

namespace Modgud.Api.Features.Auth.OAuth;

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
            .DisableAntiforgery()
            // RATE-01 — partition by client_id (60 req/min sliding window).
            .RequireRateLimiting("oauth-token");

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

        // OAUTH-14 — refuse to issue a code for a disabled client even if its
        // record still exists. Otherwise an admin "disable" would be effectively
        // ignored until the client was deleted entirely.
        if (!await IsApplicationEnabledAsync(applicationManager, application))
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.UnauthorizedClient,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The OAuth client is disabled.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        // OAUTH-14 — also refuse if any of the requested scopes is itself
        // marked Enabled=false at the realm level. Defence in depth:
        // ValidateScopeRestrictionAsync already filters scope→app linkage
        // but doesn't read the per-scope enabled flag.
        var scopeError2 = await ValidateScopesEnabledAsync(request.GetScopes(), session);
        if (scopeError2 is not null) return scopeError2;

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
            var principal = await CreateClaimsPrincipalAsync(user, request, scopeManager, userManager: userManager);

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

        // Explicit consent flow — bounce to the SPA consent page with a
        // server-side ticket id rather than a reflected returnUrl.
        //
        // Earlier shape (OAUTH-08 + OAUTH-02 + OAUTH-03): redirect carried
        // the raw authorize URL as ?returnUrl=…, the SPA round-tripped it,
        // the consent endpoint parsed client_id + scopes back out and
        // trusted whatever ApprovedScopes were submitted. Three things
        // wrong with that — open-redirect surface, scope-expansion via
        // form-payload tampering, and no subject binding so an attacker
        // could cross-site-POST consent decisions on behalf of a victim.
        //
        // Now we persist the request shape to a ConsentTicket bound to
        // the current user, hand the SPA only the ticket id, and let the
        // consent endpoint reconstruct everything server-side.
        var ticket = new ConsentTicket
        {
            Id = Guid.CreateVersion7(),
            Subject = user.Id,
            ClientId = request.ClientId!,
            RequestedScopes = request.GetScopes().ToArray(),
            AuthorizeRequestQuery = httpContext.Request.QueryString.Value ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        session.Store(ticket);
        await session.SaveChangesAsync();

        return Results.Redirect($"/consent?ticket={ticket.Id:N}");
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictScopeManager scopeManager,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IPermissionService permissionService,
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

        // OAUTH-14 — refuse to mint tokens for a disabled client / disabled
        // scope even when the request would otherwise succeed. Reject early
        // so we don't waste cycles on signature validation for a request
        // that can't possibly produce a usable token.
        if (!string.IsNullOrEmpty(request.ClientId))
        {
            var clientApp = await applicationManager.FindByClientIdAsync(request.ClientId);
            if (clientApp is not null && !await IsApplicationEnabledAsync(applicationManager, clientApp))
            {
                return ForbidInvalidGrant("The OAuth client is disabled.");
            }
        }
        var enabledScopeError = await ValidateScopesEnabledAsync(request.GetScopes(), session);
        if (enabledScopeError is not null) return enabledScopeError;

        // OAUTH-10 — RFC 6749 §10.4 / OAuth 2.1 §4.13.2 refresh-token reuse
        // detection. When a refresh token presented to /token has already been
        // redeemed, the spec mandates revoking the entire authorization chain
        // — every sibling token plus the parent authorization — because reuse
        // is the canonical "compromise" signal. OpenIddict's stock validator
        // would just return invalid_grant; we additionally tear down the chain
        // here BEFORE the validator runs, so a single misuse kills every
        // refresh and access token derived from the same authorization.
        if (request.IsRefreshTokenGrantType() && !string.IsNullOrEmpty(request.RefreshToken))
        {
            await DetectRefreshTokenReuseAsync(request.RefreshToken, tokenManager, authorizationManager);
        }

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

            // OAUTH-07 — security-stamp parity check. UserManager.UpdateSecurityStampAsync
            // is called on user-disable, password-change, role-revocation, etc.
            // (see SESSION-01 in Program.cs for the cookie side). Refresh-token
            // grant must honor the same kill-switch: if the stamp embedded in
            // the original token no longer matches the user's current stamp,
            // refuse to issue a fresh access token. Without this check a
            // stolen-or-compromised refresh token stays valid for the full
            // RefreshTokenLifetimeDays (14d default) regardless of password
            // resets and account deactivations in between.
            var tokenStamp = result.Principal?.FindFirstValue("AspNet.Identity.SecurityStamp");
            var currentStamp = await userManager.GetSecurityStampAsync(user);
            if (!string.IsNullOrEmpty(tokenStamp) &&
                !string.Equals(tokenStamp, currentStamp, StringComparison.Ordinal))
            {
                return ForbidInvalidGrant("The user's security profile has changed; please sign in again.");
            }

            var originalScopes = result.Principal?.GetScopes();
            var principal = await CreateClaimsPrincipalAsync(user, request, scopeManager, originalScopes, userManager);
            principal.SetAuthorizationId(result.Principal?.GetAuthorizationId());

            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
                ?? throw new InvalidOperationException("The application cannot be found.");

            var clientId = await applicationManager.GetClientIdAsync(application);

            // Phase 2C — resolve the linked Service Account if any. The link
            // lives on OAuthApplicationState; the OpenIddict manager doesn't
            // surface it, so go through the projection directly.
            var appState = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(x => x.ClientId == clientId && !x.IsDeleted);

            string subjectClaim;
            string? nameClaim;
            Guid? principalId = null;

            if (appState?.LinkedServiceAccountId is Guid saId)
            {
                var sa = await session.LoadAsync<ServiceAccount>(saId);
                if (sa is null || sa.IsDeleted || !sa.IsActive)
                    return ForbidInvalidGrant("The Service Account linked to this client is no longer active.");
                subjectClaim = sa.Id.ToString();
                nameClaim = sa.AccountName;
                principalId = sa.Id;
            }
            else
            {
                // Legacy fallback: an unlinked client_credentials client (only
                // seeded data or pre-Phase-2C clients before the Step 8
                // migration ran). Behaves like the legacy IdP — sub = client_id —
                // so existing M2M consumers keep working until they get
                // migrated to a Service Account.
                subjectClaim = clientId!;
                nameClaim = await applicationManager.GetDisplayNameAsync(application);
            }

            var identity = new ClaimsIdentity(
                authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                nameType: Claims.Name,
                roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, subjectClaim);
            identity.SetClaim(Claims.Name, nameClaim);
            identity.SetScopes(request.GetScopes());

            var clientResources = await scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync();
            if (!string.IsNullOrEmpty(clientId) && !clientResources.Contains(clientId))
            {
                clientResources.Add(clientId);
            }
            identity.SetResources(clientResources);

            // Phase 2C — per-audience resource_access emission on the access
            // token itself. The cc-flow has no UserInfo round-trip in practice
            // (no openid scope), so embedding the block in the JWT is the
            // only way the resource server sees the SA's permissions / roles
            // for the requested audiences. Mirrors the UserInfo behaviour
            // for human tokens.
            if (principalId is Guid pid)
            {
                var wantsRoles = identity.GetScopes().Contains(Scopes.Roles);
                var wantsPermissions = identity.GetScopes().Contains("permissions");
                var resourceAccess = await BuildResourceAccessAsync(
                    pid, clientResources, wantsRoles, wantsPermissions, session, permissionService);
                if (resourceAccess is not null)
                {
                    identity.SetClaim("resource_access",
                        System.Text.Json.JsonSerializer.SerializeToElement(resourceAccess));
                }
            }

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
        // OAUTH-15 — defense in depth: explicitly require the openid scope on
        // the access token before serving any user info. The OIDC spec scopes
        // UserInfo to tokens issued under that scope; rejecting here keeps a
        // future bug that lets non-openid tokens past the auth layer from
        // leaking profile data.
        if (!httpContext.User.HasScope(Scopes.OpenId))
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InsufficientScope,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The 'openid' scope is required for the userinfo endpoint.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

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
            // Phase 2C — Service-Account-issued tokens carry the SA's Guid as
            // sub, not an ApplicationUser id. Detect and serve a
            // machine-flavoured response: just sub + name + resource_access,
            // no email/profile claims. UserInfo for M2M is unusual (clients
            // typically don't include the openid scope) but if the request
            // does reach here the SA path is the right answer.
            if (Guid.TryParse(subject, out var saGuid))
            {
                var sa = await session.LoadAsync<ServiceAccount>(saGuid);
                if (sa is not null && !sa.IsDeleted && sa.IsActive)
                {
                    var saClaims = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [Claims.Subject] = sa.Id.ToString(),
                    };
                    if (httpContext.User.HasScope(Scopes.Profile))
                        saClaims[Claims.Name] = sa.AccountName;

                    var saResourceAccess = await BuildResourceAccessAsync(
                        sa.Id,
                        httpContext.User.GetAudiences().ToList(),
                        httpContext.User.HasScope(Scopes.Roles),
                        httpContext.User.HasScope("permissions"),
                        session, permissionService);
                    if (saResourceAccess is not null)
                        saClaims["resource_access"] = saResourceAccess;

                    return Results.Ok(saClaims);
                }
            }

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

        // Per-Audience resource_access emission per permission-modell §5.
        // For each aud in the validated access token: resolve the OAuthApi
        // by Name, find the linked App, build a block with the user's
        // bypass-pre-expanded permissions ∩ OAuthApi.PermissionIds, plus
        // roles for that App.
        //
        // Bypass-pre-expansion means: if the user holds realm:admin we
        // emit every concrete catalog string of every reachable App (the
        // realm:admin marker itself doesn't appear); if the user holds a
        // <r>:admin permission we additionally emit every <r>:<a> string
        // present in the App's catalog. Consumers do straight exact-match
        // (`permissions.includes("policy:write")`) and pick up the bypass
        // semantics for free — no PermissionEvaluator port needed on the
        // client side.
        //
        // Per-RS-subset filtering: each audience block is then narrowed to
        // OAuthApi.PermissionIds — the catalog subset the resource server
        // declared as its gating surface. Anything outside that subset is
        // not "this RS's business" and is excluded from the block. This
        // prevents permission strings from one microservice leaking into a
        // sibling's UserInfo block when both belong to the same App but
        // declare disjoint subsets.
        //
        // Audience entries that don't resolve to a registered OAuthApi
        // (e.g. the client_id fallback when no resource= was sent) are
        // silently skipped — authz info is meaningful only in the
        // context of an actual resource server.
        //
        // **Per-scope-per-claim gating.** Each authz array lands only if
        // the client opted in to its scope:
        //   - `scope=roles`       → emits `resource_access[<aud>].roles`
        //   - `scope=permissions` → emits `resource_access[<aud>].permissions`
        // Standard OIDC-style consent: the user sees each scope on the
        // consent screen and can grant/deny independently. A client that
        // never requests either gets a pure-identity UserInfo response
        // (no `resource_access` key at all). Groups stay out — pure
        // IdP-internal per permission-model, no `groups` scope.
        var wantsRoles = httpContext.User.HasScope(Scopes.Roles);
        var wantsPermissions = httpContext.User.HasScope("permissions");
        var audiences = httpContext.User.GetAudiences().ToList();

        var resourceAccess = await BuildResourceAccessAsync(
            user.Id, audiences, wantsRoles, wantsPermissions, session, permissionService);
        if (resourceAccess is not null)
            claims["resource_access"] = resourceAccess;

        return Results.Ok(claims);
    }

    /// <summary>
    /// Per-Audience <c>resource_access</c> emission shared between the
    /// UserInfo endpoint (human tokens) and the token endpoint's
    /// <c>client_credentials</c> branch (Service-Account-managed tokens).
    /// Both consume <see cref="IPermissionService"/> which is principal-id
    /// agnostic, so the same code path produces correct blocks for either.
    ///
    /// <para>Returns <c>null</c> when no audiences resolved to a registered
    /// <see cref="OAuthApiState"/>; the caller suppresses the
    /// <c>resource_access</c> key in that case (no empty object).</para>
    /// </summary>
    private static async Task<Dictionary<string, object>?> BuildResourceAccessAsync(
        Guid principalId,
        IEnumerable<string> audiences,
        bool wantsRoles,
        bool wantsPermissions,
        IDocumentSession session,
        IPermissionService permissionService)
    {
        if (!wantsRoles && !wantsPermissions) return null;

        var resourceAccess = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var audience in audiences)
        {
            var api = await session.Query<OAuthApiState>()
                .FirstOrDefaultAsync(a => a.Name == audience && !a.IsDeleted);
            if (api?.AppId is not Guid appId) continue;

            var app = await session.LoadAsync<App>(appId);
            if (app is null || app.IsDeleted) continue;

            var block = new Dictionary<string, object>(StringComparer.Ordinal);

            if (wantsPermissions)
            {
                var rawPermissions = await permissionService.GetUserPermissionsAsync(principalId, app.Slug);
                var expandedPermissions = ExpandBypassTiers(rawPermissions, app);
                var apiPermissions = NarrowToApiSubset(expandedPermissions, api, app);
                block["permissions"] = apiPermissions;
            }

            if (wantsRoles)
            {
                var rolesForApp = await permissionService.GetUserRolesAsync(principalId, app.Slug);
                block["roles"] = rolesForApp.Select(r => r.Name).ToArray();
            }

            if (block.Count > 0)
                resourceAccess[audience] = block;
        }

        return resourceAccess.Count == 0 ? null : resourceAccess;
    }

    /// <summary>
    /// Bypass-pre-expansion: take the raw permission set returned by
    /// <see cref="IPermissionService.GetUserPermissionsAsync"/> and emit
    /// only concrete catalog strings, with bypass tiers expanded:
    /// <list type="bullet">
    ///   <item><c>realm:admin</c> → every <c>App.Permissions[i].ToPermissionString()</c>
    ///   in the App's catalog. The synthetic <c>realm:admin</c> marker
    ///   itself drops out — consumers don't need it.</item>
    ///   <item><c>&lt;r&gt;:admin</c> (which IS a catalog entry) →
    ///   stays in the output, plus every <c>App.Permissions</c> with
    ///   <c>Resource == r</c> is added.</item>
    ///   <item>Any other grant → kept verbatim if it's in the App's
    ///   catalog, dropped otherwise (defensive: stale FKs from a
    ///   removed catalog entry don't leak as orphaned strings).</item>
    /// </list>
    /// Result: a deduped string array of concrete <c>&lt;resource&gt;:&lt;action&gt;</c>
    /// permissions. Consumers do <c>permissions.includes("policy:write")</c>
    /// and get correct bypass semantics for free.
    /// </summary>
    private static string[] ExpandBypassTiers(IReadOnlyList<string> rawPermissions, App app)
    {
        var catalogStrings = app.Permissions
            .Select(p => p.ToPermissionString())
            .ToHashSet(StringComparer.Ordinal);

        var emit = new HashSet<string>(StringComparer.Ordinal);

        // realm:admin trumps everything — emit the whole catalog and stop.
        if (rawPermissions.Contains(PermissionEvaluator.RealmAdminPermission))
        {
            foreach (var s in catalogStrings) emit.Add(s);
            return [.. emit];
        }

        // Non-realm-admin: walk the user's grants. For every <r>:admin we
        // see, also pull in every <r>:<a> in the catalog. Direct grants
        // are kept iff they're in the catalog — orphaned strings (e.g.
        // from a stale projection) drop out.
        foreach (var grant in rawPermissions)
        {
            if (catalogStrings.Contains(grant)) emit.Add(grant);

            var parts = grant.Split(':');
            if (parts.Length == 2 && parts[1] == PermissionEvaluator.AdminAction)
            {
                var resource = parts[0];
                foreach (var entry in app.Permissions.Where(p => p.Resource == resource))
                    emit.Add(entry.ToPermissionString());
            }
        }

        return [.. emit];
    }

    /// <summary>
    /// Narrows the bypass-expanded permission set to the resource-server's
    /// declared subset (<see cref="OAuthApiState.PermissionIds"/>).
    /// Anything outside the subset is excluded — that's the whole point of
    /// the per-RS PermissionIds field: the RS opts in to the slice of the
    /// App catalog it actually gates on, and UserInfo emits exactly that
    /// slice intersected with the user's grants.
    ///
    /// <para>Empty subset = empty emission. A freshly-created
    /// <see cref="OAuthApiState"/> with no PermissionIds set hasn't opted in
    /// to any catalog entries yet, so it gets nothing — the admin must
    /// explicitly tick the catalog entries this RS gates on.</para>
    /// </summary>
    private static string[] NarrowToApiSubset(string[] expandedPermissions, OAuthApiState api, App app)
    {
        if (api.PermissionIds.Count == 0) return Array.Empty<string>();

        var apiSubsetStrings = app.Permissions
            .Where(p => api.PermissionIds.Contains(p.Id))
            .Select(p => p.ToPermissionString())
            .ToHashSet(StringComparer.Ordinal);

        return expandedPermissions
            .Where(s => apiSubsetStrings.Contains(s))
            .ToArray();
    }

    /// <summary>
    /// OIDC RP-Initiated Logout 1.0 §2 — end-session endpoint. Hardened against
    /// every concern <c>C5</c> in the security-hardening tracker raised:
    ///
    /// <list type="bullet">
    ///   <item><description><b>OAUTH-04 / CSRF-01</b> — requires <c>id_token_hint</c>.
    ///   Without it, refuse. The hint serves as the CSRF defence: an attacker
    ///   forcing a victim to hit this endpoint via <c>&lt;img&gt;</c> doesn't
    ///   know the victim's id_token, so can't construct a valid request.</description></item>
    ///   <item><description><b>OAUTH-04 (cont.)</b> — validates that the
    ///   <c>id_token_hint</c>'s <c>sub</c> matches the current cookie session.
    ///   Refuses if a different user is presenting someone else's hint.</description></item>
    ///   <item><description><b>OAUTH-04 (cont.)</b> — validates
    ///   <c>post_logout_redirect_uri</c> with EXACT-match against the
    ///   authoring client's registered URIs (no prefix matching, no wildcards).
    ///   Drops the hard-coded <c>"/"</c> redirect that ignored what the RP
    ///   asked for.</description></item>
    ///   <item><description><b>SESSION-02</b> — revokes every active OAuth
    ///   token + authorization for this (subject, client) pair. Without this,
    ///   a logged-out user's previously issued refresh tokens stayed valid
    ///   until natural expiry, defeating the contractual meaning of "logout".</description></item>
    ///   <item><description><b>OAUTH-18</b> — keeps GET supported (RFC's
    ///   recommended verb for end-session) but the id_token_hint requirement
    ///   makes the GET form non-CSRF-able by construction.</description></item>
    /// </list>
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        SignInManager<ApplicationUser> signInManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictTokenManager tokenManager)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect end-session request cannot be retrieved.");

        // OAUTH-04 / CSRF-01 — id_token_hint is mandatory. Without it, anyone
        // can craft a logout link and force the victim's session to die.
        if (string.IsNullOrEmpty(request.IdTokenHint))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "id_token_hint required",
                detail: "The end-session endpoint requires an id_token_hint per " +
                        "OpenID Connect RP-Initiated Logout 1.0. Use /api/account/logout " +
                        "for IdP-internal logout (cookie-only).");
        }

        // OpenIddict already validates the hint's signature (against the realm's
        // signing keys, courtesy of RealmTokenValidationHandler) and emits the
        // claims as a principal under its server scheme — we just authenticate
        // through that scheme to read them.
        var hintAuth = await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var hintSubject = hintAuth.Principal?.GetClaim(Claims.Subject);
        var hintClientId = hintAuth.Principal?.GetClaim(Claims.Audience)
                           ?? hintAuth.Principal?.GetAudiences().FirstOrDefault();

        // Reject anything that didn't end up with a real subject claim. The
        // OpenIddict server scheme is forgiving — it can return a "successful"
        // result with an empty/anonymous principal when the hint is malformed
        // or unverifiable. Treating that as a valid hint would let an attacker
        // pass any garbage string and still trigger the sign-out path.
        if (!hintAuth.Succeeded ||
            hintAuth.Principal?.Identity?.IsAuthenticated != true ||
            string.IsNullOrEmpty(hintSubject))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid id_token_hint",
                detail: "The id_token_hint could not be validated.");
        }

        // Subject-binding: if the user has an active cookie session, it MUST
        // match the hint's subject. Otherwise we'd let an attacker who
        // somehow obtained a victim's id_token sign the victim out remotely
        // even though our cookie says "different user is here".
        var cookieAuth = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (cookieAuth.Succeeded && cookieAuth.Principal is not null)
        {
            var cookieSubject = cookieAuth.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                                ?? cookieAuth.Principal.FindFirstValue(Claims.Subject);
            if (!string.IsNullOrEmpty(cookieSubject) &&
                !string.IsNullOrEmpty(hintSubject) &&
                !string.Equals(cookieSubject, hintSubject, StringComparison.Ordinal))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "id_token_hint does not match the current session",
                    detail: "Cannot end a session that does not belong to the presented id_token_hint.");
            }
        }

        // Resolve the calling RP from the hint's audience. We need it twice:
        // (a) to validate post_logout_redirect_uri against its registered set
        // and (b) to scope token-revocation to that client.
        object? application = null;
        if (!string.IsNullOrEmpty(hintClientId))
        {
            application = await applicationManager.FindByClientIdAsync(hintClientId);
        }

        // OAUTH-04 (cont.) — exact-match validation of post_logout_redirect_uri.
        // RFC 6749 §3.1.2 (re-applied per OIDC RP-Initiated Logout) — no prefix
        // matching, no wildcards, byte-for-byte equality only.
        string? validatedRedirect = null;
        if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri))
        {
            if (application is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "post_logout_redirect_uri without resolvable client",
                    detail: "id_token_hint did not name a known client; cannot validate the redirect URI.");
            }

            var registered = await applicationManager.GetPostLogoutRedirectUrisAsync(application);
            if (!registered.Any(u => string.Equals(u, request.PostLogoutRedirectUri, StringComparison.Ordinal)))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "post_logout_redirect_uri not registered",
                    detail: "The supplied post_logout_redirect_uri is not in the client's registered set.");
            }
            validatedRedirect = request.PostLogoutRedirectUri;
        }

        // SESSION-02 — revoke every token + authorization for this
        // (subject, client) pair. Refresh tokens that were issued before the
        // logout become invalid immediately, so a user who logs out gets
        // the contractual guarantee that their previously issued tokens are
        // dead.
        if (!string.IsNullOrEmpty(hintSubject) && application is not null)
        {
            var clientPrimaryKey = await applicationManager.GetIdAsync(application);
            if (!string.IsNullOrEmpty(clientPrimaryKey))
            {
                await foreach (var token in tokenManager.FindAsync(
                    subject: hintSubject, client: clientPrimaryKey, status: null, type: null))
                {
                    await tokenManager.TryRevokeAsync(token);
                }
                await foreach (var auth in authorizationManager.FindAsync(
                    subject: hintSubject, client: clientPrimaryKey, status: null, type: null, scopes: default))
                {
                    await authorizationManager.TryRevokeAsync(auth);
                }
            }
        }

        await signInManager.SignOutAsync();

        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = validatedRedirect },
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    private static async Task<ClaimsPrincipal> CreateClaimsPrincipalAsync(
        ApplicationUser user,
        OpenIddictRequest request,
        IOpenIddictScopeManager scopeManager,
        IEnumerable<string>? scopeOverrides = null,
        UserManager<ApplicationUser>? userManager = null)
    {
        // Identity must use the OpenIddict default authentication type so it processes
        // the claims correctly (Identity's ApplicationScheme identity is filtered out).
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString());

        // OAUTH-07 — embed the user's current security stamp on the principal.
        // Persisted with the refresh-token (server-side reference token store),
        // NOT emitted into access/id tokens (see GetDestinations — yields
        // nothing for AspNet.Identity.SecurityStamp). On refresh, the stamp
        // is compared to the user's current value; mismatch → invalid_grant.
        if (userManager is not null)
        {
            var stamp = await userManager.GetSecurityStampAsync(user);
            if (!string.IsNullOrEmpty(stamp))
            {
                identity.SetClaim("AspNet.Identity.SecurityStamp", stamp);
            }
        }

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

        principal.SetDestinations(GetDestinations);
        return principal;
    }

    private static string GetDisplayName(ApplicationUser user)
        => AuthorizationEndpointHelpers.GetDisplayName(user);

    private static IEnumerable<string> GetDestinations(Claim claim)
        => AuthorizationEndpointHelpers.GetDestinations(claim);

    /// <summary>
    /// OAUTH-14 — read the <c>cocoar:enabled</c> property off the OAuth
    /// application. Missing → treated as enabled (matches the legacy
    /// default-true semantics from before the property was introduced).
    /// </summary>
    private static async Task<bool> IsApplicationEnabledAsync(
        IOpenIddictApplicationManager applicationManager,
        object application)
    {
        var properties = await applicationManager.GetPropertiesAsync(application);
        if (properties.TryGetValue(
                Modgud.Domain.OAuth.Applications.OAuthApplicationPropertyKeys.Enabled,
                out var element))
        {
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.True => true,
                _ => true, // Unknown shape — fail open to current behaviour
            };
        }
        return true;
    }

    /// <summary>
    /// OAUTH-14 — refuse the request if any requested scope is marked
    /// <c>Enabled=false</c> in the per-tenant <see cref="OAuthScopeState"/>
    /// document. Standard OIDC scopes (openid/email/profile/...) are
    /// seeded with Enabled=true and never get flipped, so this only
    /// rejects when an admin explicitly disabled a custom scope.
    /// </summary>
    private static async Task<IResult?> ValidateScopesEnabledAsync(
        IEnumerable<string> requestedScopes,
        IDocumentSession session)
    {
        var requested = requestedScopes.Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0) return null;

        var disabled = await session.Query<OAuthScopeState>()
            .Where(s => !s.IsDeleted && !s.Enabled && requested.Contains(s.Name))
            .Select(s => s.Name)
            .ToListAsync();

        if (disabled.Count == 0) return null;

        return Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidScope,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                    $"The following scope(s) are disabled: {string.Join(", ", disabled)}.",
            }),
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    /// <summary>
    /// OAUTH-10 — refresh-token reuse detection per RFC 6749 §10.4. If the
    /// presented reference refresh token is already marked redeemed in the
    /// store, that's the textbook compromise indicator: the legitimate
    /// holder MUST have moved on to the rotated successor token, so a
    /// re-presentation means an attacker captured the original. Revoke
    /// everything — every token in the chain plus the parent authorization
    /// — so neither the attacker nor any previously-issued sibling can
    /// continue to act on the user's behalf.
    /// <para>
    /// Idempotent: a token already revoked stays revoked. Best-effort: on
    /// any storage error during the revoke walk we log-and-swallow rather
    /// than escalate, because the OpenIddict pipeline that runs right
    /// after this will still reject the request with invalid_grant — the
    /// chain teardown is hardening, not a correctness gate.
    /// </para>
    /// </summary>
    private static async Task DetectRefreshTokenReuseAsync(
        string refreshTokenValue,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager)
    {
        var token = await tokenManager.FindByReferenceIdAsync(refreshTokenValue);
        if (token is null) return;

        var status = await tokenManager.GetStatusAsync(token);
        if (!string.Equals(status, Statuses.Redeemed, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var authorizationId = await tokenManager.GetAuthorizationIdAsync(token);
        if (string.IsNullOrEmpty(authorizationId)) return;

        // Revoke every token in the chain.
        await foreach (var sibling in tokenManager.FindByAuthorizationIdAsync(authorizationId))
        {
            try { await tokenManager.TryRevokeAsync(sibling); }
            catch { /* best-effort */ }
        }

        // Revoke the authorization itself so a fresh OAuth flow on the same
        // client+subject pair must go through the consent + grant cycle
        // again rather than reusing this compromised authorization.
        var authorization = await authorizationManager.FindByIdAsync(authorizationId);
        if (authorization is not null)
        {
            try { await authorizationManager.TryRevokeAsync(authorization); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Resolves the app slugs the bearer token's calling client is linked
    /// to: looks up the OAuth client by <c>client_id</c>, walks its
    /// <c>AppIds</c> list, and returns the slugs of the (non-deleted) Apps.
    /// Returns an empty list when no client_id is on the principal
    /// (interactive cookie auth), the client is not found, or the client
    /// has no App link. Used by UserInfo to populate
    /// <c>resource_access</c> per Keycloak convention.
    /// </summary>
    private static async Task<List<string>> ResolveAppSlugsForClientAsync(
        ClaimsPrincipal user, IDocumentSession session)
    {
        var clientId = user.FindFirst(Claims.ClientId)?.Value;
        if (string.IsNullOrEmpty(clientId)) return [];

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted);
        if (client is null || client.AppIds.Count == 0) return [];

        var apps = await session.Query<App>()
            .Where(a => client.AppIds.Contains(a.Id) && !a.IsDeleted)
            .ToListAsync();
        return apps.Select(a => a.Slug).ToList();
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

        // Resolve the calling client. We need it twice: once for the
        // app-link set, once for the IsDynamicallyRegistered flag.
        OAuthApplicationState? client = null;
        if (!string.IsNullOrEmpty(clientId))
        {
            client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted);
        }

        // DCR clients have no AppIds by design — they're realm-wide
        // public PKCE clients minted via /connect/register. The
        // triple-opt-in design uses per-scope `AllowDynamicRegistrationClients`
        // as the boundary instead of the app-link check: an app-scoped
        // scope is reachable by a DCR client iff the realm-admin opted
        // it in via that flag. The audience-containment handler
        // additionally requires `resource=` to point at an opted-in
        // OAuthApi, so the security primitive holds without needing
        // the client to share an App with the scope.
        var isDcrClient = client is not null
            && ReadDcrFlag(client.Properties, OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered);

        if (isDcrClient)
        {
            var notOptedIn = appScoped.FirstOrDefault(s =>
                !ReadDcrFlag(s.Properties, ScopePropertyKeys.AllowDynamicRegistrationClients));
            if (notOptedIn is not null)
            {
                return Results.Forbid(
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidScope,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                            $"Scope '{notOptedIn.Name}' is not opted in for Dynamic Client Registration clients. " +
                            "Ask the realm admin to enable AllowDynamicRegistrationClients on the scope, " +
                            "or use a global (cross-app) scope.",
                    }),
                    new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
            }
            return null;
        }

        // Non-DCR client: app-scoped scope must intersect the client's
        // own App set. (Global scopes — AppId == null — were filtered
        // out above.)
        var clientAppIds = client?.AppIds.ToHashSet() ?? new HashSet<Guid>();
        var bad = appScoped.FirstOrDefault(s => !clientAppIds.Contains(s.AppId!.Value));
        if (bad is not null)
        {
            var description = clientAppIds.Count == 0
                ? $"Scope '{bad.Name}' is restricted to a specific app, but the calling client is not linked to any."
                : $"Scope '{bad.Name}' is not in the calling client's app set.";
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

    /// <summary>Reads a cocoar:* boolean from a Marten-serialised
    /// Properties dict. The value may come back as a plain bool
    /// (Newtonsoft default) OR a JsonElement depending on the
    /// serializer the host happens to use — both cases handled.</summary>
    private static bool ReadDcrFlag(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return false;
        return raw switch
        {
            bool b => b,
            System.Text.Json.JsonElement el when el.ValueKind is System.Text.Json.JsonValueKind.True => true,
            _ => false,
        };
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
