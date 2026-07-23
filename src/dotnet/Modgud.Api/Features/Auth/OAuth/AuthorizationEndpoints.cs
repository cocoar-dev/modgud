using System.Security.Claims;
using System.Text.Json;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Apis;
using Modgud.Permissions;
using Modgud.Permissions.Abstractions;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Consent;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.OpenIddict.Cimd;
using Modgud.Infrastructure.Persistence.Tenancy;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;
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
/// logout. Role + permission and custom-claim injection are fully wired:
/// <see cref="CreateClaimsPrincipalAsync"/> builds the principal and per-audience
/// <c>resource_access</c> block via <see cref="IPermissionService"/>, then stamps
/// claim destinations with <c>SetDestinations</c>.
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
        CimdClientResolver cimdResolver,
        IDocumentSession session)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // ADR-0011 — first-signal-consistency: if we entered on an Application
        // subdomain, the client must belong to that App (or be realm-wide).
        // Runs first so a cross-app client is rejected before any other work.
        var consistencyError = await ValidateFirstSignalConsistencyAsync(
            httpContext, request.ClientId, session, cimdResolver, httpContext.RequestAborted);
        if (consistencyError is not null) return consistencyError;

        // Stufe-3 scope restriction: an app-scoped scope (Scope.AppId != null)
        // can only be requested by a client linked to the same App. Standard
        // OIDC scopes (openid/email/profile/roles/offline_access) are seeded
        // with AppId=null and therefore always pass.
        var scopeError = await ValidateScopeRestrictionAsync(
            request.ClientId, request.GetScopes(), session, cimdResolver, httpContext.RequestAborted);
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

        // Consent-deny re-entry: /connect/consent redirects a DENIED decision
        // back here with ?deny_ticket={id} so OpenIddict emits the RFC 6749
        // access_denied error to the client's redirect_uri honoring its
        // response_mode + RFC 9207 iss — symmetric with the approve path, which
        // re-enters authorize to complete the grant. We require a genuine denied,
        // subject-bound ticket before acting; a forged/mismatched deny_ticket
        // falls through to the normal flow (re-prompts consent), leaking nothing.
        if ((string?)request.GetParameter("deny_ticket") is { Length: > 0 } denyTicketRaw
            && Guid.TryParseExact(denyTicketRaw, "N", out var denyTicketId))
        {
            var deniedTicket = await session.LoadAsync<ConsentTicket>(denyTicketId);
            if (deniedTicket is { DeniedAt: not null } && deniedTicket.Subject == user.Id)
            {
                return Results.Forbid(
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user denied the authorization request.",
                    }),
                    new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
            }
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

        // CIMD: the synthesized client is ConsentType=explicit, so
        // a first authorize always lands on the consent screen — which shows
        // the client_id hostname + "unverified" marker as the phishing
        // mitigation. Subsequent authorizes for the same user+client+scopes
        // auto-approve via the remembered authorization, exactly like every
        // other client (DCR included). We deliberately do NOT force consent
        // on every authorize: the post-consent re-entry to /authorize relies
        // on this same shortcut to complete the round-trip, so forcing it
        // would loop the user back to /consent indefinitely.
        if (consentType == ConsentTypes.Implicit || authorizations.Count != 0)
        {
            var principal = await CreateClaimsPrincipalAsync(
                user, request, scopeManager, userManager: userManager,
                cookiePrincipal: authResult.Principal);

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
        IOpenIddictScopeManager scopeManager,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IPermissionService permissionService,
        IClientSessionService clientSessionService,
        CimdClientResolver cimdResolver,
        IEmailOtpService emailOtpService,
        RealmScopedFido2Factory fido2Factory,
        RpIdResolver rpIdResolver,
        IApplicationSettingsResolver applicationSettingsResolver,
        IDocumentSession session)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // ADR-0011 — first-signal-consistency (mirror of the authorize check, and
        // the only gate for client_credentials which skips authorize entirely):
        // a request on an Application subdomain must present a client that belongs
        // to that App or is realm-wide.
        var consistencyError = await ValidateFirstSignalConsistencyAsync(
            httpContext, request.ClientId, session, cimdResolver, httpContext.RequestAborted);
        if (consistencyError is not null) return consistencyError;

        // Stufe-3 scope restriction. Code/refresh-grant scopes were already
        // validated at /connect/authorize time, but defence in depth — and
        // client_credentials skips the authorize step entirely so we MUST
        // validate here too.
        var scopeError = await ValidateScopeRestrictionAsync(
            request.ClientId, request.GetScopes(), session, cimdResolver, httpContext.RequestAborted);
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
        // detection. With SetRefreshTokenReuseLeeway(TimeSpan.Zero) (see
        // OpenIddictExtensions), OpenIddict's own stock
        // Protection.ValidateTokenEntry handler detects a redeemed-token
        // replay and revokes the whole token family + parent authorization
        // during authentication-middleware processing — before routing even
        // reaches this delegate. Auditing (security event + warning log) is
        // recorded by RefreshTokenReuseAuditHandler, which runs immediately
        // before that stock handler.

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

            // OAUTH-07 — security-stamp parity check. The stamp is rotated on
            // password change, password reset, account deactivate/delete, and
            // explicit force-logout (see SESSION-01 in Program.cs for the cookie
            // side). Audit #11: RBAC mutations (group/role/permission changes) are
            // FK-based and do NOT rotate the stamp — a demotion is therefore picked
            // up at the NEXT refresh, which re-reads durable permissions via
            // BakeFederatedResourceAccessAsync, not instantly. Refresh-token grant
            // honors the stamp kill-switch: if the stamp embedded in the original
            // token no longer matches the user's current stamp, refuse to issue a
            // fresh access token. Without this check a stolen-or-compromised refresh
            // token stays valid for the full RefreshTokenLifetimeDays (14d default)
            // regardless of password resets and account deactivations in between.
            var tokenStamp = result.Principal?.FindFirstValue("AspNet.Identity.SecurityStamp");
            var currentStamp = await userManager.GetSecurityStampAsync(user);
            if (!string.IsNullOrEmpty(tokenStamp) &&
                !string.Equals(tokenStamp, currentStamp, StringComparison.Ordinal))
            {
                return ForbidInvalidGrant("The user's security profile has changed; please sign in again.");
            }

            var originalScopes = result.Principal?.GetScopes();
            var principal = await CreateClaimsPrincipalAsync(
                user, request, scopeManager, originalScopes, userManager,
                cookiePrincipal: result.Principal);

            var authorizationId = result.Principal?.GetAuthorizationId();
            if (request.IsRefreshTokenGrantType())
            {
                var rawClientSessionId = result.Principal?
                    .FindFirstValue(SessionClaimTypes.ClientSessionId);
                if (!Guid.TryParse(rawClientSessionId, out var clientSessionId) ||
                    string.IsNullOrEmpty(request.ClientId) ||
                    await clientSessionService.ValidateAndTouchAsync(
                        user.Id,
                        clientSessionId,
                        request.ClientId,
                        authorizationId,
                        httpContext.RequestAborted) is null)
                {
                    return ForbidInvalidGrant("The client session has expired or was revoked; please sign in again.");
                }

                principal.SetAuthorizationId(authorizationId);
                principal.SetClaim(SessionClaimTypes.ClientSessionId, clientSessionId.ToString());
            }
            else if (principal.HasScope(Scopes.OfflineAccess))
            {
                var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
                    ?? throw new InvalidOperationException("The application cannot be found.");
                var clientPk = await applicationManager.GetIdAsync(application) ?? string.Empty;
                var sessionAuthorization = await authorizationManager.CreateAsync(
                    principal: principal,
                    subject: user.Id.ToString(),
                    client: clientPk,
                    type: AuthorizationTypes.AdHoc,
                    scopes: principal.GetScopes());
                authorizationId = await authorizationManager.GetIdAsync(sessionAuthorization)
                    ?? throw new InvalidOperationException("The client-session authorization has no id.");
                principal.SetAuthorizationId(authorizationId);

                var clientSession = await clientSessionService.CreateAsync(
                    new CreateClientSessionRequest(
                        user.Id,
                        request.ClientId!,
                        clientPk,
                        authorizationId,
                        await applicationManager.GetDisplayNameAsync(application),
                        httpContext.Connection.RemoteIpAddress?.ToString(),
                        httpContext.Request.Headers.UserAgent.ToString()),
                    httpContext.RequestAborted);
                principal.SetClaim(SessionClaimTypes.ClientSessionId, clientSession.Id.ToString());
            }
            else
            {
                // No refresh token will be issued, so this is not a long-lived
                // client/device session. Keep the authorization produced by the
                // code/device flow and do not add an orphan ClientSession row.
                principal.SetAuthorizationId(authorizationId);
            }

            if (principal.HasScope(Scopes.OfflineAccess))
            {
                var clientSessionPolicy = await clientSessionService.ResolvePolicyAsync(
                    request.ClientId!, httpContext.RequestAborted);
                principal.SetRefreshTokenLifetime(clientSessionPolicy.IdleLifetime);
            }

            // Federation v1.1: bake the federated resource_access (durable ∪
            // session-derived) into the access token HERE, while the carrier is
            // still on the principal. Needed for BOTH client types — OpenIddict
            // strips the no-destination carrier from the access token (and from the
            // reference payload), so it can't be read back at UserInfo for either.
            await BakeFederatedResourceAccessAsync(
                principal, user.Id, request, session, permissionService);

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
                // Hub-boundary defense-in-depth (decision D): the cc-flow never
                // copies the session-group carrier (fresh identity, no cookie), but
                // pin it to NO destination here too so any future drift that adds
                // it can't fall through to the access-token default and leak.
                FederationClaimTypes.SessionGroup => Array.Empty<string>(),
                _ => new[] { Destinations.AccessToken },
            });

            return Results.SignIn(new ClaimsPrincipal(identity), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // ADR-0010 — native (cookieless) passwordless token grants. A clean
        // per-grant_type branch (Phasing rule 1) so the passkey grant (phase 2)
        // is an additive entry, not surgery. Each grant verifies its factor
        // server-side and mints tokens via the same principal -> SignIn pipeline
        // the code/refresh grant uses; no cookie, no browser.
        if (string.Equals(request.GrantType, CocoarGrantTypes.Otp, StringComparison.Ordinal))
        {
            return await ExchangeNativeOtpAsync(
                request, httpContext, applicationSettingsResolver, session, userManager, signInManager, scopeManager,
                applicationManager, authorizationManager, permissionService,
                clientSessionService, emailOtpService, httpContext.RequestAborted);
        }

        if (string.Equals(request.GrantType, CocoarGrantTypes.Magic, StringComparison.Ordinal))
        {
            return await ExchangeNativeMagicAsync(
                request, httpContext, applicationSettingsResolver, session, userManager, signInManager, scopeManager,
                applicationManager, authorizationManager, permissionService,
                clientSessionService, httpContext.RequestAborted);
        }

        if (string.Equals(request.GrantType, CocoarGrantTypes.Passkey, StringComparison.Ordinal))
        {
            return await ExchangeNativePasskeyAsync(
                request, httpContext, applicationSettingsResolver, session, userManager, signInManager, scopeManager,
                applicationManager, authorizationManager, permissionService,
                clientSessionService, fido2Factory, rpIdResolver, httpContext.RequestAborted);
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

    // ─────────────────────────── ADR-0010 native grants ───────────────────────

    /// <summary>Uniform OAuth error for the native grants — mirrors the local
    /// <c>ForbidInvalidGrant</c> but takes the error code too (factor failures
    /// use <c>invalid_grant</c>; a disabled realm uses <c>unsupported_grant_type</c>).</summary>
    private static IResult ForbidNativeGrant(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });

    /// <summary>Reads the effective (App ⊕ realm) native-grant settings for the
    /// request (ADR-0011). Client_id-time: the App comes from the Host pin when
    /// present, else the calling client's single App binding. Returns the settings
    /// ONLY when the master flag is on; null otherwise (never-configured reads as
    /// disabled).</summary>
    private static async Task<NativeGrantSettings?> LoadNativeGrantSettingsAsync(
        IApplicationSettingsResolver settingsResolver, HttpContext httpContext, string? clientId, CancellationToken ct)
    {
        var settings = await settingsResolver.ResolveForRequestAsync(httpContext, clientId, ct);
        return settings.NativeGrants is { Enabled: true } ng ? ng : null;
    }

    /// <summary>Second-factor gate for the native grants. Returns null when the
    /// user has no TOTP factor (nothing owed) or the supplied <c>totp_code</c> is
    /// valid; a Forbid result when a required code is missing or invalid. Called
    /// AFTER the primary factor verifies, so a clear "2FA required/invalid" error
    /// is not a user-existence oracle (the caller already proved factor possession).</summary>
    private static async Task<IResult?> CheckTwoFactorAsync(
        ApplicationUser user, OpenIddictRequest request, UserManager<ApplicationUser> userManager)
    {
        if (!user.TwoFactorEnabled) return null;

        var totp = ((string?)request.GetParameter("totp_code"))?.Replace(" ", "").Replace("-", "");
        if (string.IsNullOrEmpty(totp))
            return ForbidNativeGrant(Errors.InvalidGrant, "Two-factor authentication is required; supply totp_code.");

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, totp);
        return valid ? null : ForbidNativeGrant(Errors.InvalidGrant, "The two-factor code is invalid.");
    }

    /// <summary>Shared token-mint pipeline for the native grants: builds the same
    /// ClaimsPrincipal the code/refresh grant builds (sub, scopes, destinations,
    /// security stamp — so the refresh-time OAUTH-07 kill-switch applies), bakes
    /// federated resource_access, ensures a permanent (subject, client)
    /// authorization (parity with AuthorizeAsync) and applies the short native
    /// access-token TTL before the cookieless SignIn that mints the tokens.</summary>
    private static async Task<IResult> IssueNativeGrantAsync(
        ApplicationUser user,
        OpenIddictRequest request,
        IOpenIddictScopeManager scopeManager,
        UserManager<ApplicationUser> userManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IDocumentSession session,
        IPermissionService permissionService,
        IClientSessionService clientSessionService,
        HttpContext httpContext,
        NativeGrantSettings nativeSettings)
    {
        // userManager (NOT a plain session load) so the security stamp is
        // populated on the principal — without it the OAUTH-07 parity check
        // silently no-ops and the minted refresh chain escapes revocation.
        var principal = await CreateClaimsPrincipalAsync(
            user, request, scopeManager, scopeOverrides: null, userManager, cookiePrincipal: null);

        await BakeFederatedResourceAccessAsync(principal, user.Id, request, session, permissionService);

        // Each native device/login gets its own ad-hoc authorization. Consent is
        // still represented by the permanent authorization created by the web
        // flow; this authorization is solely the independently revocable token
        // family root for one ClientSession.
        var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The application cannot be found.");

        var subject = user.Id.ToString();
        var clientPk = await applicationManager.GetIdAsync(application) ?? string.Empty;
        var authorization = await authorizationManager.CreateAsync(
            principal: principal, subject: subject, client: clientPk,
            type: AuthorizationTypes.AdHoc, scopes: principal.GetScopes());
        var authorizationId = await authorizationManager.GetIdAsync(authorization)
            ?? throw new InvalidOperationException("The client-session authorization has no id.");
        principal.SetAuthorizationId(authorizationId);

        if (principal.HasScope(Scopes.OfflineAccess))
        {
            var clientSession = await clientSessionService.CreateAsync(
                new CreateClientSessionRequest(
                    user.Id,
                    request.ClientId!,
                    clientPk,
                    authorizationId,
                    await applicationManager.GetDisplayNameAsync(application),
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    httpContext.Request.Headers.UserAgent.ToString()),
                httpContext.RequestAborted);
            principal.SetClaim(SessionClaimTypes.ClientSessionId, clientSession.Id.ToString());
        }

        // ADR-0010 — short JWT access TTL for native clients (per-realm tunable,
        // validated at write time). Clamp defensively so even a settings doc
        // written outside the validated patch path can never mint an unbounded /
        // zero-lifetime JWT — the short TTL is the only bound on a non-revocable
        // JWT access token. The refresh token stays a revocable reference token.
        principal.SetAccessTokenLifetime(
            ClampLifetime(nativeSettings.AccessTokenLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60)));
        if (principal.HasScope(Scopes.OfflineAccess))
        {
            var clientSessionPolicy = await clientSessionService.ResolvePolicyAsync(
                request.ClientId!, httpContext.RequestAborted);
            principal.SetRefreshTokenLifetime(clientSessionPolicy.IdleLifetime);
        }

        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static TimeSpan ClampLifetime(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>Uniform <c>invalid_grant</c> for a native-grant FACTOR failure,
    /// with a 100-300ms jitter so the response time carries no email-existence
    /// signal (an unknown email returns without the extra challenge-load work a
    /// known email does; the jitter dominates that sub-millisecond delta). Mirrors
    /// the anti-timing discipline of the magic-link / native OTP request endpoints.</summary>
    private static async Task<IResult> ForbidFactorFailureAsync(string description)
    {
#pragma warning disable CA5394, SCS0005
        await Task.Delay(Random.Shared.Next(100, 300));
#pragma warning restore CA5394, SCS0005
        return ForbidNativeGrant(Errors.InvalidGrant, description);
    }

    /// <summary><c>urn:cocoar:otp</c> — verify an email + one-time code (reusing
    /// <see cref="IEmailOtpService.VerifyOtpAsync"/>) and mint tokens. Every proof
    /// failure returns the SAME <c>invalid_grant</c> "Invalid or expired code."
    /// with a uniform jitter — anti-enumeration on both the body AND timing
    /// channels (an unknown email skips the extra challenge-load work a known one
    /// does). The per-client (client_id-partitioned) token-endpoint rate limit
    /// bounds brute force.</summary>
    private static async Task<IResult> ExchangeNativeOtpAsync(
        OpenIddictRequest request,
        HttpContext httpContext,
        IApplicationSettingsResolver settingsResolver,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IOpenIddictScopeManager scopeManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IPermissionService permissionService,
        IClientSessionService clientSessionService,
        IEmailOtpService emailOtpService,
        CancellationToken ct)
    {
        var nativeSettings = await LoadNativeGrantSettingsAsync(settingsResolver, httpContext, request.ClientId, ct);
        if (nativeSettings is null)
            return ForbidNativeGrant(Errors.UnsupportedGrantType, "This grant type is not enabled for this realm.");
        if (string.IsNullOrEmpty(request.ClientId))
            return ForbidNativeGrant(Errors.InvalidClient, "client_id is required.");

        var email = request.Username;
        var code = (string?)request.GetParameter("otp_code");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return await ForbidFactorFailureAsync("Invalid or expired code.");

        // Store-backed lookup so the security stamp is populated (see IssueNativeGrantAsync).
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
            return await ForbidFactorFailureAsync("Invalid or expired code.");

        // Defence-in-depth: native email-OTP is a PRIMARY factor, so a confirmed
        // mailbox is required at the minting site too (not only at the request
        // endpoint). ADR-0011 exception: a passwordless, still-unconfirmed account
        // is a native JIT registration — the OTP redeem itself proves the mailbox,
        // so it is allowed and confirmed on success below. A password-bearing
        // unconfirmed account must verify via the web link (never gets a native
        // code issued, and is rejected here as before).
        var isPasswordlessRegistration = !user.EmailConfirmed && string.IsNullOrEmpty(user.PasswordHash);
        if (!user.EmailConfirmed && !isPasswordlessRegistration)
            return await ForbidFactorFailureAsync("Invalid or expired code.");

        var verify = await emailOtpService.VerifyOtpAsync(user.Id, code, ct);
        if (verify.IsError)
            return await ForbidFactorFailureAsync("Invalid or expired code.");

        // A consumed OTP proves mailbox control — auto-confirm a JIT registration
        // (parity with the magic-link grant's mailbox-proof confirm).
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            session.Store(user);
            session.Events.Append(user.Id, new Modgud.Domain.Users.Events.UserUpdatedEvent(
                Id: user.Id, Firstname: default, Lastname: default, Acronym: default, Email: default));
            await session.SaveChangesAsync(ct);
        }

        // Second factor only after the primary factor proved possession.
        var twoFactor = await CheckTwoFactorAsync(user, request, userManager);
        if (twoFactor is not null) return twoFactor;

        if (!await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
            return ForbidNativeGrant(Errors.InvalidGrant, "The account cannot sign in.");

        return await IssueNativeGrantAsync(
            user, request, scopeManager, userManager, applicationManager,
            authorizationManager, session, permissionService, clientSessionService,
            httpContext, nativeSettings);
    }

    /// <summary><c>urn:cocoar:magic</c> — verify a magic-link (user_id + token)
    /// against the single-use <see cref="MagicLinkChallenge"/> and mint tokens.
    /// Mirrors the web /magic-link/login verify (shared hash, single-use delete,
    /// mailbox-proof auto-confirm) minus the cookie SignIn. Uniform
    /// <c>invalid_grant</c> on every proof failure.</summary>
    private static async Task<IResult> ExchangeNativeMagicAsync(
        OpenIddictRequest request,
        HttpContext httpContext,
        IApplicationSettingsResolver settingsResolver,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IOpenIddictScopeManager scopeManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IPermissionService permissionService,
        IClientSessionService clientSessionService,
        CancellationToken ct)
    {
        var nativeSettings = await LoadNativeGrantSettingsAsync(settingsResolver, httpContext, request.ClientId, ct);
        if (nativeSettings is null)
            return ForbidNativeGrant(Errors.UnsupportedGrantType, "This grant type is not enabled for this realm.");
        if (string.IsNullOrEmpty(request.ClientId))
            return ForbidNativeGrant(Errors.InvalidClient, "client_id is required.");

        var uidRaw = (string?)request.GetParameter("user_id");
        var token = (string?)request.GetParameter("magic_token");
        if (!Guid.TryParse(uidRaw, out var userId) || string.IsNullOrWhiteSpace(token))
            return ForbidNativeGrant(Errors.InvalidGrant, "Invalid or expired link.");

        var hash = MagicLinkChallenge.HashToken(token);
        // IsConsumed must be checked HERE too, not just in the web flow: the web
        // redemption marks the challenge consumed rather than deleting it (so the
        // version-checked Store can win the concurrency race), which left a link
        // already used in the browser still redeemable through this native grant.
        var challenge = await session.Query<MagicLinkChallenge>()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.TokenHash == hash, ct);
        if (challenge is null || challenge.IsExpired || challenge.IsConsumed)
        {
            if (challenge is not null) { session.Delete(challenge); await session.SaveChangesAsync(ct); }
            return ForbidNativeGrant(Errors.InvalidGrant, "Invalid or expired link.");
        }

        // Store-backed lookup so the security stamp is populated.
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
        {
            session.Delete(challenge);
            await session.SaveChangesAsync(ct);
            return ForbidNativeGrant(Errors.InvalidGrant, "Invalid or expired link.");
        }

        // A consumed magic link proves mailbox control — auto-confirm (parity
        // with the web flow) + push the SignalR projection refresh.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            session.Store(user);
            session.Events.Append(user.Id, new Modgud.Domain.Users.Events.UserUpdatedEvent(
                Id: user.Id, Firstname: default, Lastname: default, Acronym: default, Email: default));
        }

        // Second factor only after the link proved mailbox possession. The link
        // is single-use: consume it even when the second factor is still owed, so
        // a known-good link cannot be reused to brute-force the TOTP.
        var twoFactor = await CheckTwoFactorAsync(user, request, userManager);
        if (twoFactor is not null)
        {
            session.Delete(challenge);
            await session.SaveChangesAsync(ct);
            return twoFactor;
        }

        session.Delete(challenge);
        await session.SaveChangesAsync(ct);

        return await IssueNativeGrantAsync(
            user, request, scopeManager, userManager, applicationManager,
            authorizationManager, session, permissionService, clientSessionService,
            httpContext, nativeSettings);
    }

    /// <summary><c>urn:cocoar:passkey</c> — verify a WebAuthn assertion against a
    /// server-side ceremony (issued by <c>POST /connect/passkey/begin</c>) and mint
    /// tokens. The ceremony is single-use (consumed before verifying so a captured
    /// id can't be replayed). A UserVerification passkey is itself multi-factor
    /// (device possession + biometric/PIN), so — unlike the otp/magic grants — this
    /// does NOT additionally demand a totp_code. Uniform jittered <c>invalid_grant</c>
    /// on every proof failure.</summary>
    private static async Task<IResult> ExchangeNativePasskeyAsync(
        OpenIddictRequest request,
        HttpContext httpContext,
        IApplicationSettingsResolver settingsResolver,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IOpenIddictScopeManager scopeManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IPermissionService permissionService,
        IClientSessionService clientSessionService,
        RealmScopedFido2Factory fido2Factory,
        RpIdResolver rpIdResolver,
        CancellationToken ct)
    {
        var nativeSettings = await LoadNativeGrantSettingsAsync(settingsResolver, httpContext, request.ClientId, ct);
        if (nativeSettings is null)
            return ForbidNativeGrant(Errors.UnsupportedGrantType, "This grant type is not enabled for this realm.");
        if (string.IsNullOrEmpty(request.ClientId))
            return ForbidNativeGrant(Errors.InvalidClient, "client_id is required.");

        var ceremonyRaw = (string?)request.GetParameter("ceremony_id");
        var assertionJson = (string?)request.GetParameter("assertion");
        if (!Guid.TryParse(ceremonyRaw, out var ceremonyId))
            return await ForbidFactorFailureAsync("Invalid or expired passkey ceremony.");

        var ceremony = await session.LoadAsync<PasskeyCeremony>(ceremonyId, ct);
        if (ceremony is null || ceremony.IsExpired || ceremony.IsConsumed)
        {
            if (ceremony is not null && ceremony.IsExpired)
            {
                session.Delete(ceremony);
                await session.SaveChangesAsync(ct);
            }
            return await ForbidFactorFailureAsync("Invalid or expired passkey ceremony.");
        }

        // Single-use: consume ANY presented live ceremony as soon as it resolves —
        // before the assertion-presence check and the verify — so a captured
        // ceremony_id can never be replayed, even when paired with a
        // missing/garbage assertion. This is a VERSION-CHECKED Store of the
        // ConsumedAt marker, not a Delete: Marten does not version-check deletes,
        // so two concurrent redemptions of one ceremony_id would otherwise both
        // proceed and each mint a token. The loser's SaveChangesAsync throws.
        ceremony.ConsumedAt = DateTimeOffset.UtcNow;
        session.Store(ceremony);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            return await ForbidFactorFailureAsync("Invalid or expired passkey ceremony.");
        }

        // ADR-0009 per-client RP-ID: a ceremony begun for a specific client may only
        // be redeemed by that same client. Skipped for a legacy/realm-scoped ceremony
        // (ClientId == null). This keeps token-authorization provenance unambiguous
        // even when two clients legitimately share one RP ID (where the crypto
        // rpIdHash check alone would not distinguish them).
        if (!string.IsNullOrEmpty(ceremony.ClientId)
            && !string.Equals(ceremony.ClientId, request.ClientId, StringComparison.Ordinal))
            return await ForbidFactorFailureAsync("Invalid or expired passkey ceremony.");

        if (string.IsNullOrWhiteSpace(assertionJson))
            return await ForbidFactorFailureAsync("Invalid or expired passkey ceremony.");

        // Rebuild the relying party with EXACTLY the RP ID pinned at begin — never
        // re-resolved — so verify validates against the same RP ID the authenticator
        // signed. PrimaryDomain is the fallback for a legacy/realm-scoped ceremony.
        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        var activeRpId = string.IsNullOrWhiteSpace(ceremony.RpId) ? primaryDomain : ceremony.RpId;

        // Accept the origin the authenticator actually signed (scoped in
        // BuildConfiguration to this RP-ID's own subdomains) so a per-client RP-ID
        // that is a registrable suffix of the app origin still verifies. Malformed
        // input yields no extra origin; the shared verifier then fails closed.
        string[]? presentedOrigins = null;
        try
        {
            var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                assertionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (RealmFido2.TryGetClientDataOrigin(assertion?.Response?.ClientDataJson) is { } origin)
                presentedOrigins = [origin];
        }
        catch (JsonException) { /* leave null — verifier fails closed below */ }

        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: activeRpId, additionalOrigins: presentedOrigins);
        }
        catch (RelyingPartyUnavailableException)
        {
            return ForbidNativeGrant(Errors.InvalidGrant, "Passkey sign-in is not available for this realm.");
        }

        AssertionOptions options;
        try
        {
            options = AssertionOptions.FromJson(ceremony.OptionsJson);
        }
        catch
        {
            return await ForbidFactorFailureAsync("Invalid or expired passkey ceremony.");
        }

        // Shared FIDO2 verify — the SAME path the web cookie flow uses (no fork).
        // Resolves + counter-advances the StoredPasskeyCredential, or null on any
        // failure (bad assertion, unknown credential, signature/origin mismatch).
        // Scoped to the active RP ID (ADR-0009): a credential enrolled under another
        // app's RP ID is never even considered here.
        var storedCredential = await PasskeyAssertionVerifier.VerifyAsync(
            fido2, options, assertionJson, session, activeRpId, primaryDomain, ct);
        if (storedCredential is null)
            return await ForbidFactorFailureAsync("Passkey verification failed.");

        // Store-backed lookup so the security stamp is populated (see IssueNativeGrantAsync).
        var user = await userManager.FindByIdAsync(storedCredential.UserId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
            return await ForbidFactorFailureAsync("Passkey verification failed.");

        // No CheckTwoFactorAsync: a UserVerification passkey already satisfies MFA
        // (the begin endpoint requires UV), so we do not additionally demand totp_code.
        return await IssueNativeGrantAsync(
            user, request, scopeManager, userManager, applicationManager,
            authorizationManager, session, permissionService, clientSessionService,
            httpContext, nativeSettings);
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

        // Audit #9 — re-check account state before serving identity/authz, mirroring
        // the token-exchange guard (:~291). FindByIdAsync already filters IsDeleted,
        // but a client that opted into JWT access tokens holds a self-validating token
        // with no revocable store doc — so a freshly DEACTIVATED user's unexpired JWT
        // would otherwise keep reading email/profile + resource_access for a full
        // access-token lifetime. (Reference-token clients are already cut off by the
        // revoker; this closes the JWT window.)
        if (!user.IsActive || user.IsDeleted)
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user account is no longer active.",
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

        // Federation: the federated resource_access (durable ∪ session-derived) is
        // baked into the access token at issuance for both client types (see
        // BakeFederatedResourceAccessAsync) — OpenIddict strips the no-destination
        // carrier from the access token, so it can't be recomputed from the carrier
        // here. Echo the token's own block verbatim so UserInfo and the token agree.
        // For a reference token the block lives in the server-side payload (opaque on
        // the wire); for a JWT it rides the token. The recompute branch is a fallback
        // for tokens that carry no baked block (e.g. minted before v1.1).
        var bakedResourceAccess = httpContext.User.GetClaim("resource_access");
        if (!string.IsNullOrEmpty(bakedResourceAccess))
        {
            claims["resource_access"] =
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(bakedResourceAccess);
        }
        else
        {
            var sessionGroupIds = ReadSessionGroupIds(httpContext.User);
            var resourceAccess = await BuildResourceAccessAsync(
                user.Id, audiences, wantsRoles, wantsPermissions, session, permissionService, sessionGroupIds);
            if (resourceAccess is not null)
                claims["resource_access"] = resourceAccess;
        }

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
        IPermissionService permissionService,
        IReadOnlyCollection<Guid>? sessionGroupIds = null)
    {
        if (!wantsRoles && !wantsPermissions) return null;

        // Federation v1 (decision D): the single union call site. For human
        // UserInfo this carries the session-derived group IDs read off the
        // access-token principal; for the cc-flow + Service-Account paths it is
        // empty, so the union overloads behave identically to the no-arg ones.
        var sessionIds = sessionGroupIds ?? Array.Empty<Guid>();

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
                var rawPermissions = await permissionService.GetUserPermissionsAsync(principalId, app.Slug, sessionIds);
                var expandedPermissions = ExpandBypassTiers(rawPermissions, app);
                var apiPermissions = NarrowToApiSubset(expandedPermissions, api, app);
                block["permissions"] = apiPermissions;
            }

            if (wantsRoles)
            {
                var rolesForApp = await permissionService.GetUserRolesAsync(principalId, app.Slug, sessionIds);
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

        // Federation v1 invariant (decision G): realm:admin is hard local-only.
        // This expander receives flat permission strings with no source tag, so it
        // CANNOT distinguish a local realm:admin from an externally-derived one —
        // a session-sourced realm:admin would expand the whole catalog here. The
        // provenance-aware strip therefore lives upstream in
        // PermissionService.GetUserPermissionsAsync (the union overload only adds
        // realm:admin for durable groups); by the time a realm:admin string
        // reaches this method it is guaranteed local. Do NOT relax that.
        //
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

    internal static async Task<ClaimsPrincipal> CreateClaimsPrincipalAsync(
        ApplicationUser user,
        OpenIddictRequest request,
        IOpenIddictScopeManager scopeManager,
        IEnumerable<string>? scopeOverrides = null,
        UserManager<ApplicationUser>? userManager = null,
        ClaimsPrincipal? cookiePrincipal = null)
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

        // Federation v1 (decision D/E): copy the session-group carrier claim(s)
        // from the cookie/grant principal onto this grant. One claim per matched
        // ExternallyDrivable group GUID. GetDestinations yields nothing for this
        // type, so SetDestinations below leaves it with NO destination — it is
        // persisted in the server-side reference token (read back at UserInfo and
        // re-baked at refresh) but never emitted on the wire (hub boundary).
        //   • Authorize path passes the live cookie principal → reflects this
        //     login's freshly-derived groups.
        //   • Refresh/code/device path passes the rehydrated reference-token
        //     principal → re-copies the FROZEN set, no recompute. The session is
        //     the lease (decision E).
        if (cookiePrincipal is not null)
        {
            foreach (var carrier in cookiePrincipal.FindAll(FederationClaimTypes.SessionGroup))
            {
                identity.AddClaim(new Claim(FederationClaimTypes.SessionGroup, carrier.Value));
            }

            // RFC 9449 §5 — carry the DPoP refresh-token binding forward. On the
            // refresh path cookiePrincipal is the rehydrated reference-token
            // principal; re-copying its bound-key thumbprint onto this grant keeps
            // the rotated refresh token sender-constrained, and lets
            // DpopRefreshTokenBindingHandler compare it against the proof presented
            // at this refresh. GetDestinations yields nothing for it (hub-internal,
            // like the session-group carrier). Nothing to copy on the initial
            // authorize/code path — the binding is established at the token exchange.
            var boundJkt = cookiePrincipal.FindFirstValue(
                Modgud.Infrastructure.OpenIddict.Dpop.DpopConstants.RefreshBindingClaimType);
            if (!string.IsNullOrEmpty(boundJkt))
            {
                identity.AddClaim(new Claim(
                    Modgud.Infrastructure.OpenIddict.Dpop.DpopConstants.RefreshBindingClaimType, boundJkt));
            }
        }

        principal.SetDestinations(GetDestinations);
        return principal;
    }

    private static string GetDisplayName(ApplicationUser user)
        => AuthorizationEndpointHelpers.GetDisplayName(user);

    /// <summary>
    /// Federation v1.1 — bake the per-audience <c>resource_access</c> block (durable
    /// ∪ session-derived) into the access token at issuance, for BOTH reference and
    /// JWT clients.
    ///
    /// <para>The session-group carrier is a no-destination claim, and OpenIddict's
    /// <c>PrepareAccessTokenPrincipal</c> strips every no-destination claim before
    /// building the access token — including the copy persisted with a reference
    /// token. So the carrier is NOT readable back at UserInfo for reference clients
    /// either (the original v1 "lazy recompute at UserInfo" assumption was wrong).
    /// We therefore compute the union HERE, while the carrier is still on the
    /// issuance principal, and embed only the RESULT — the permissions/roles the RS
    /// is entitled to — as a normal (access-token-destined) claim:</para>
    /// <list type="bullet">
    ///   <item>JWT clients: it rides the self-contained token (RS reads it directly).</item>
    ///   <item>Reference clients: it survives the strip (access-token destination),
    ///   is persisted in the server-side reference payload, stays opaque on the wire,
    ///   and is echoed at UserInfo.</item>
    /// </list>
    /// The carrier itself never gains a destination, so the hub boundary holds: only
    /// the rendered result ever leaves, never the raw group IDs.
    ///
    /// <para>Audiences come from the requested <c>resource=</c> indicators when
    /// present — exactly what <see cref="ResourceIndicatorHandler"/> narrows the
    /// token's <c>aud</c> to — so the baked blocks match the token's audience set and
    /// never over-share. Consistent with decision E (the lease): the set is frozen
    /// for the token's life and re-baked at refresh (durable re-read, session
    /// re-copied frozen). Reference tokens additionally keep instant revocation.</para>
    /// </summary>
    private static async Task BakeFederatedResourceAccessAsync(
        ClaimsPrincipal principal,
        Guid userId,
        OpenIddictRequest request,
        IDocumentSession session,
        IPermissionService permissionService)
    {
        var wantsRoles = principal.HasScope(Scopes.Roles);
        var wantsPermissions = principal.HasScope("permissions");
        if (!wantsRoles && !wantsPermissions) return;

        // The token's aud after ResourceIndicatorHandler == the requested
        // resource= set (validated to be a subset of the granted resources); fall
        // back to the scope-derived set when no indicator was sent. Either way this
        // equals what lands on the token's aud, so the blocks won't over-share.
        var requested = request.GetResources().ToList();
        var audiences = requested.Count > 0 ? requested : principal.GetResources().ToList();

        var resourceAccess = await BuildResourceAccessAsync(
            userId, audiences, wantsRoles, wantsPermissions, session, permissionService,
            ReadSessionGroupIds(principal));
        if (resourceAccess is null) return;

        var identity = (ClaimsIdentity)principal.Identity!;
        identity.SetClaim("resource_access",
            System.Text.Json.JsonSerializer.SerializeToElement(resourceAccess));

        // Re-stamp destinations so the freshly-added claim routes to the access
        // token (GetDestinations default case). SetDestinations only stamps the
        // claims present when it runs, and CreateClaimsPrincipalAsync already ran
        // it before this claim existed.
        principal.SetDestinations(GetDestinations);
    }

    private static IEnumerable<string> GetDestinations(Claim claim)
        => AuthorizationEndpointHelpers.GetDestinations(claim);

    /// <summary>
    /// Federation v1 (decision D) — reads the internal <c>modgud:session-group</c>
    /// carrier off a principal and parses each value to a group GUID. One claim
    /// per group; malformed values are skipped defensively. Returns an empty set
    /// when none are present (password / JWT-access / non-federated logins).
    /// </summary>
    private static IReadOnlyCollection<Guid> ReadSessionGroupIds(ClaimsPrincipal principal)
    {
        List<Guid>? ids = null;
        foreach (var claim in principal.FindAll(FederationClaimTypes.SessionGroup))
        {
            if (Guid.TryParse(claim.Value, out var id))
                (ids ??= []).Add(id);
        }
        return ids is null ? Array.Empty<Guid>() : ids;
    }

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
        string? clientId, IEnumerable<string> requestedScopes, IDocumentSession session,
        CimdClientResolver cimdResolver, CancellationToken cancellationToken)
    {
        var scopeNames = requestedScopes?.ToArray() ?? Array.Empty<string>();
        if (scopeNames.Length == 0) return null;

        // Load scope projections by name once — far fewer DB hits than per-scope.
        var scopes = await session.Query<OAuthScopeState>()
            .Where(s => scopeNames.Contains(s.Name) && !s.IsDeleted)
            .ToListAsync(cancellationToken);

        // If no requested scope is app-scoped, there's nothing to restrict.
        var appScoped = scopes.Where(s => s.AppId.HasValue).ToList();
        if (appScoped.Count == 0) return null;

        // Resolve the calling client. We need it twice: once for the
        // app-link set, once for the IsDynamicallyRegistered flag.
        OAuthApplicationState? client = null;
        if (!string.IsNullOrEmpty(clientId))
        {
            client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, cancellationToken);

            // CIMD clients are non-persisted; resolve the synthesized client so
            // the dynamic-registration scope-opt-in path below applies to them
            // (the synthetic client carries DcrIsDynamicallyRegistered=true).
            client ??= await cimdResolver.ResolveAsync(clientId, cancellationToken);
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

    /// <summary>
    /// ADR-0011 first-signal-consistency invariant. When the request arrived on
    /// an Application subdomain (the Host pinned an App — Phase 1), the presented
    /// client must belong to that Application OR be realm-wide (empty
    /// <c>AppIds</c>). A client bound to a <em>different</em> App is a cross-app
    /// confusion / confused-deputy surface and is rejected. No Host pin = the
    /// <c>client_id</c> is the first signal and there is nothing to reconcile.
    /// </summary>
    private static async Task<IResult?> ValidateFirstSignalConsistencyAsync(
        HttpContext httpContext, string? clientId, IDocumentSession session,
        CimdClientResolver cimdResolver, CancellationToken cancellationToken)
    {
        if (httpContext.GetApplicationId() is not { } pinnedApplicationId) return null;
        if (string.IsNullOrEmpty(clientId)) return null;

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, cancellationToken);
        // CIMD clients are non-persisted + realm-wide (no AppIds) — resolve so the
        // realm-wide allowance below applies rather than treating them as unknown.
        client ??= await cimdResolver.ResolveAsync(clientId, cancellationToken);

        // Unknown client: not our concern — the standard client validation rejects it.
        if (client is null) return null;

        if (!AuthorizationEndpointHelpers.IsCrossAppViolation(pinnedApplicationId, client.AppIds))
            return null;

        return Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidRequest,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                    "The client is not associated with the application for this origin.",
            }),
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
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
    /// ADR-0011 first-signal-consistency decision (pure). Given the App pinned by
    /// the request Host (Phase 1) and the presented client's App set, returns
    /// <c>true</c> iff this is a cross-app violation that must be rejected:
    /// <list type="bullet">
    ///   <item>no Host-pinned App → no violation (client_id leads);</item>
    ///   <item>client has empty <c>AppIds</c> (realm-wide) → no violation;</item>
    ///   <item>client's <c>AppIds</c> contains the pinned App → consistent;</item>
    ///   <item>client is bound only to other App(s) → <b>violation</b>.</item>
    /// </list>
    /// </summary>
    public static bool IsCrossAppViolation(Guid? hostPinnedApplicationId, IReadOnlyCollection<Guid> clientAppIds)
    {
        if (hostPinnedApplicationId is not { } applicationId) return false;
        if (clientAppIds.Count == 0) return false;
        return !clientAppIds.Contains(applicationId);
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
            case SessionClaimTypes.ClientSessionId:
                yield break;

            // Federation v1 (hub boundary, decision D): the session-group carrier
            // is INTERNAL — it rides the server-side reference token and is unioned
            // into resource_access at UserInfo/token time, but must NEVER reach the
            // wire. Yield nothing for either token (exactly like SecurityStamp).
            case FederationClaimTypes.SessionGroup:
                yield break;

            // RFC 9449 §5 — the DPoP refresh-token binding carrier. Internal: it is
            // persisted in the server-side refresh token so the binding survives
            // rotation, but must never reach an access/id token (a resource server
            // reads the binding from cnf.jkt). Yield nothing (like SecurityStamp).
            case Modgud.Infrastructure.OpenIddict.Dpop.DpopConstants.RefreshBindingClaimType:
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
