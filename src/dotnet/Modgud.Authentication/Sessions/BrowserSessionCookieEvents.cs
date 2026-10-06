using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication.SignIn;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// Makes <see cref="UserSession"/> authoritative for every Modgud application
/// cookie, including login paths that bypass <c>SignInManager</c>.
/// </summary>
public sealed class BrowserSessionCookieEvents(ISessionService sessions) : CookieAuthenticationEvents
{
    public override async Task SigningIn(CookieSigningInContext context)
    {
        var principal = context.Principal;
        var userId = ParseUserId(principal);
        if (principal is null || userId is null)
            throw new InvalidOperationException("An application cookie cannot be issued without a user id.");

        UserSession? browserSession = null;
        // RefreshSignInAsync rebuilds the principal and may drop custom claims.
        // Fall back to the currently authenticated request so a profile/stamp
        // refresh keeps the same authoritative session instead of creating a
        // duplicate row.
        var currentClaim = principal.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value
            ?? context.HttpContext.User.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
        if (Guid.TryParse(currentClaim, out var currentSessionId))
            browserSession = await sessions.ValidateSessionAsync(
                userId.Value, currentSessionId, touch: false, context.HttpContext.RequestAborted);

        if (browserSession is null)
        {
            var created = await sessions.CreateSessionAsync(
                userId.Value,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                context.HttpContext.Request.Headers.UserAgent.ToString(),
                context.HttpContext.RequestAborted);
            if (created.IsError)
                throw new InvalidOperationException(created.FirstError.Description);
            browserSession = created.Value;
        }

        var identity = principal.Identities.FirstOrDefault(i => i.IsAuthenticated)
            ?? throw new InvalidOperationException("An application cookie requires an authenticated identity.");
        foreach (var old in principal.FindAll(SessionClaimTypes.BrowserSessionId).ToList())
            old.Subject?.RemoveClaim(old);
        identity.AddClaim(new Claim(SessionClaimTypes.BrowserSessionId, browserSession.Id.ToString()));

        StampSignInFactors(context, identity, principal, userId.Value);

        var policy = await sessions.GetPolicyAsync(context.HttpContext.RequestAborted);
        if (!policy.AllowRememberMe)
            context.Properties.IsPersistent = false;
        context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
        context.Properties.ExpiresUtc = browserSession.ExpiresAt;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var userId = ParseUserId(context.Principal);
        var rawSessionId = context.Principal?.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
        if (userId is null || !Guid.TryParse(rawSessionId, out var sessionId))
        {
            await RejectAsync(context);
            return;
        }

        // Storage exceptions intentionally escape: a transient database outage
        // fails the request but does not turn into a destructive cookie delete.
        var session = await sessions.ValidateSessionAsync(
            userId.Value, sessionId, touch: true, context.HttpContext.RequestAborted);
        if (session is null)
        {
            await RejectAsync(context);
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }

    public override async Task SigningOut(CookieSigningOutContext context)
    {
        var principal = context.HttpContext.User;
        var userId = ParseUserId(principal);
        var rawSessionId = principal.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
        if (userId is not null && Guid.TryParse(rawSessionId, out var sessionId))
        {
            // ADR 0021 — a sign-out is a "logout" end; the end-session endpoint names the
            // relying party that asked for it so that RP is not notified about itself.
            var initiator = context.HttpContext.Items[BackChannelLogout.BackChannelLogoutConstants.InitiatingClientItem] as string;
            await sessions.EndSessionAsync(
                userId.Value, sessionId, Events.AccessEndReasons.Logout, initiator, context.HttpContext.RequestAborted);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        RedirectOrStatusAsync(context, StatusCodes.Status401Unauthorized);

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        RedirectOrStatusAsync(context, StatusCodes.Status403Forbidden);

    /// <summary>
    /// ADR 0025 — stamp what this sign-in proved. Sources, unioned:
    /// <list type="bullet">
    ///   <item>the factors the endpoint declared for this request;</item>
    ///   <item>factors already on the principal being signed in;</item>
    ///   <item>the factors of the same user's current session — a second sign-in by the
    ///   same user (step-up, <c>RefreshSignInAsync</c> after a profile change) raises the
    ///   session, it never resets it. A different user signing in starts from scratch.</item>
    /// </list>
    /// A federated principal arrives with its factors already on it (ExternalLoginProcessor).
    /// </summary>
    private static void StampSignInFactors(
        CookieSigningInContext context, ClaimsIdentity identity, ClaimsPrincipal principal, Guid userId)
    {
        var factors = SignInAssurance.ReadFactors(principal);

        var declared = SignInAssurance.TakeDeclared(context.HttpContext);
        if (declared is not null)
            factors = SignInAssurance.Union(factors, declared);

        var current = context.HttpContext.User;
        if (current.Identity?.IsAuthenticated == true && ParseUserId(current) == userId)
            factors = SignInAssurance.Union(factors, SignInAssurance.ReadFactors(current));

        SignInAssurance.Stamp(identity, factors);
    }

    private static Guid? ParseUserId(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }

    private static Task RedirectOrStatusAsync(
        RedirectContext<CookieAuthenticationOptions> context,
        int apiStatusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = apiStatusCode;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
