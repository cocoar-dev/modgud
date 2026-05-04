using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Cocoar.Auth.Authentication.Domain.LoginProviders;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

public static class ExternalAuthEndpoints
{
    public static void MapExternalAuthEndpoints(this IEndpointRouteBuilder endpoints, string path)
    {
        // Public — login page needs the list to render buttons. Only returns
        // enabled, non-deleted, Oidc-typed providers. Internal is rendered by
        // the built-in form, not as a button on this list; Saml/Ldap/Kerberos
        // are not yet wired and stay hidden until a future phase plugs them in.
        endpoints.MapGet($"{path}/account/external-logins",
            async ([FromServices] IQuerySession session, CancellationToken ct) =>
            {
                var providers = await session.Query<LoginProvider>()
                    .Where(c => !c.IsDeleted && c.Enabled && c.Type == LoginProviderType.Oidc)
                    .ToListAsync(ct);

                return Results.Ok(providers.Select(c => new ExternalLoginDto(
                    Id: c.Id,
                    DisplayName: c.DisplayName,
                    Flavor: c.Flavor,
                    IconName: c.IconName,
                    ButtonColorHex: c.ButtonColorHex)).ToArray());
            }).AllowAnonymous();

        // Start login flow — issues OIDC challenge to the IdP. Redirects to
        // the IdP's authorize endpoint. Returns 404 if the provider is not
        // enabled (no silent enumeration).
        endpoints.MapGet($"{path}/account/external-login/{{loginProviderId:guid}}/start",
            async (Guid loginProviderId,
                   string? returnUrl,
                   HttpContext http,
                   [FromServices] IQuerySession session,
                   CancellationToken ct) =>
            {
                var config = await session.LoadAsync<LoginProvider>(loginProviderId, ct);
                if (config is null || config.IsDeleted || !config.Enabled)
                    return Results.NotFound();

                // Internal is invisible to this surface (no silent enumeration);
                // Saml/Ldap/Kerberos are intentionally surfaced as "not yet
                // supported" so admins/CI can tell the difference between
                // "wrong id" and "type not implemented".
                if (config.Type == LoginProviderType.Internal)
                    return Results.NotFound();
                if (config.Type != LoginProviderType.Oidc)
                    return Results.BadRequest(new
                    {
                        Code = LoginProviderErrors.TypeNotSupported(config.Type).Code,
                        Message = LoginProviderErrors.TypeNotSupported(config.Type).Description,
                    });

                var schemeName = DynamicOidcSchemeManager.SchemeNameFor(loginProviderId);
                // Return URL lives in Items so the finish endpoint honors it
                // AFTER processing; RedirectUri itself must point at finish
                // because OIDC handler redirects there straight out of the
                // callback with the External cookie already set.
                var props = new AuthenticationProperties
                {
                    RedirectUri = "/api/account/external-login/finish",
                    Items =
                    {
                        ["loginProviderId"] = loginProviderId.ToString("N"),
                        ["returnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl!,
                    },
                };
                return Results.Challenge(props, [schemeName]);
            }).AllowAnonymous();

        // RP-initiated logout: after Cocoar.Auth's local cookie is already cleared
        // (by /account/logout), this endpoint redirects the browser to the IdP's
        // end_session_endpoint. OIDC middleware builds the URL (includes
        // post_logout_redirect_uri + id_token_hint when available).
        //
        // LOGOUT-01: anonymous by design (the local cookie is gone by the
        // time we reach here), but a same-site Origin / Referer check
        // prevents a malicious third-party site from triggering an upstream
        // OIDC logout for the victim. Internal nav from /login or /profile
        // sends an Origin header matching the IdP's host; cross-origin
        // forced loads do not.
        endpoints.MapGet($"{path}/account/external-logout/{{loginProviderId:guid}}",
            async (Guid loginProviderId, HttpContext http) =>
            {
                if (!IsSameSiteRequest(http))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Cross-origin external-logout blocked",
                        detail: "External logout must originate from the IdP's own UI.");
                }

                var schemeName = DynamicOidcSchemeManager.SchemeNameFor(loginProviderId);
                var props = new AuthenticationProperties { RedirectUri = "/login" };
                return Results.SignOut(props, [schemeName]);
            }).AllowAnonymous();

        // Finish endpoint: runs after OIDC middleware drops the ticket into
        // the External cookie. Processes the external principal (user
        // matching, JIT creation, claim-snapshot persistence) and issues the
        // Identity.Application cookie. Anonymous because the caller has no
        // app cookie yet; the External cookie is the gate.
        endpoints.MapGet($"{path}/account/external-login/finish",
            async (HttpContext http,
                   [FromServices] ExternalLoginProcessor processor,
                   [FromServices] Microsoft.AspNetCore.Identity.SignInManager<Cocoar.Auth.Authentication.Domain.ApplicationUser> signInManager,
                   [FromServices] Cocoar.Auth.Authentication.Sessions.ISessionService sessionService,
                   CancellationToken ct) =>
            {
                var auth = await http.AuthenticateAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);
                if (!auth.Succeeded || auth.Principal is null)
                    return Results.Redirect("/login?error=oidc-no-ticket");

                if (!auth.Properties!.Items.TryGetValue("loginProviderId", out var loginProviderIdValue)
                    || !Guid.TryParseExact(loginProviderIdValue, "N", out var loginProviderId))
                {
                    return Results.Redirect("/login?error=oidc-no-idp");
                }

                // If the app cookie is already present, this is a link-flow
                // initiated from Profile → Security. Pass the authenticated
                // user id down so the processor binds the external identity
                // to that account instead of JIT-creating a new one.
                var existingAuth = await http.AuthenticateAsync(
                    Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);
                Guid? authenticatedUserId = null;
                if (existingAuth.Succeeded && existingAuth.Principal is not null)
                {
                    var idClaim = existingAuth.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (Guid.TryParse(idClaim, out var parsed)) authenticatedUserId = parsed;
                }

                var result = await processor.ProcessAsync(auth.Principal, loginProviderId, ct, authenticatedUserId);
                if (!result.Succeeded)
                {
                    await http.SignOutAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);
                    var code = Uri.EscapeDataString(result.ErrorCode ?? "unknown");
                    return Results.Redirect($"/login?error={code}");
                }

                // Sign in with the app cookie. Persistent=true gives the OIDC
                // path the same 30-day sliding lifetime as Passkey/Magic-Link.
                await http.SignInAsync(
                    Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme,
                    result.Principal!,
                    new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });

                // Discard the short-lived External ticket now that we have
                // the application cookie — defense against stale claim-replay.
                await http.SignOutAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);

                // Track per-user device session (best-effort).
                var signedInIdClaim = result.Principal!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (Guid.TryParse(signedInIdClaim, out var signedInUserId))
                    await Cocoar.Auth.Authentication.Sessions.SessionTracker.RecordLoginAsync(sessionService, http, signedInUserId, ct);

                var returnUrl = auth.Properties.Items.TryGetValue("returnUrl", out var ru) && !string.IsNullOrWhiteSpace(ru)
                    ? ru
                    : "/";
                return Results.Redirect(returnUrl);
            }).AllowAnonymous();
    }

    public record ExternalLoginDto(
        Guid Id,
        string DisplayName,
        string Flavor,
        string? IconName,
        string? ButtonColorHex);

    /// <summary>
    /// Returns true when the request looks like a same-site navigation:
    /// either the Origin or the Referer header matches the request host.
    /// Used as a lightweight CSRF gate on anonymous endpoints that trigger
    /// outbound OIDC redirects (LOGOUT-01) — a real logout from our /login
    /// or /profile UI sends one of these headers; an attacker's
    /// <c>&lt;img src=…&gt;</c> typically does not.
    /// </summary>
    private static bool IsSameSiteRequest(HttpContext ctx)
    {
        var host = ctx.Request.Host.ToString();
        if (string.IsNullOrEmpty(host)) return false;

        bool MatchesHost(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return false;
            if (!Uri.TryCreate(headerValue, UriKind.Absolute, out var uri)) return false;
            var headerHost = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return string.Equals(headerHost, host, StringComparison.OrdinalIgnoreCase);
        }

        var origin = ctx.Request.Headers.Origin.ToString();
        if (MatchesHost(origin)) return true;

        var referer = ctx.Request.Headers.Referer.ToString();
        if (MatchesHost(referer)) return true;

        return false;
    }
}
