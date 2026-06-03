using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Observability;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// Server-side 2FA enforcement. Without this, a user who logs in with just a password at
/// AuthenticationMinimumLevel >= 1 would get a valid auth cookie and full API access, even
/// though the SPA shows the blocking setup modal. The frontend is never the enforcement
/// boundary — a curl, an old tab, or a modified client must also be blocked.
///
/// The middleware returns 403 to any non-whitelisted request when:
///   AuthenticationMinimumLevel &gt;= 1 AND the user has no 2FA method AND the grace
///   period is null (with days == 0) or in the past.
///
/// Setup endpoints are whitelisted so users stuck on the blocking modal can still enroll
/// a 2FA method, check their identity, or log out.
/// </summary>
public class TwoFactorEnforcementMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Paths callable while grace-locked. All start with "/api/account/" — the account
    /// feature area is what lets a user recover without leaving the login screen. Matched
    /// case-insensitively via StartsWith so "/api/account/mfa/setup" passes "/api/account/mfa/".
    /// </summary>
    private static readonly string[] AllowedPathPrefixes =
    [
        "/api/account/me",
        "/api/account/logout",
        "/api/account/mfa/",
        "/api/account/email-otp/",
        "/api/account/passkey/",
        "/api/account/change-password",
        // Docs stay readable even under grace-lock — a user locked out of the app still
        // needs to look up how to set up 2FA. The /docs branch has its own auth-gate,
        // so anonymous requests don't slip through here.
        "/docs/",
        "/docs",
    ];

    public async Task InvokeAsync(
        HttpContext context,
        IAuthSettings settings,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager)
    {
        if (settings.AuthenticationMinimumLevel < 1)
        {
            await next(context);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Anonymous endpoints (app-info, login, magic-link request, forgot-password, health, …)
        // must stay reachable even if the caller's cookie points at a user past grace. Otherwise
        // the SPA can't even load the login page after we've redirected it here — infinite loop.
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (IsWhitelisted(path))
        {
            await next(context);
            return;
        }

        // Federated MFA: the ExternalLoginProcessor preserves Entra/Okta's amr
        // claim onto the session cookie as modgud.external.amr. If the IdP
        // already asserted a multi-factor sign-in for THIS session, treat it
        // as equivalent to having local 2FA for the duration of the session —
        // no SecureSetupModal, no grace check. The user still must configure
        // local 2FA for non-federated login paths (magic-link, password fallback).
        //
        // Accepted amr values per RFC 8176: "mfa" (generic multi-factor), "otp"
        // (one-time password), "fido" (WebAuthn / FIDO2), "hwk" (proof-of-
        // possession of a hardware-secured key), "swk" (software-secured key),
        // "mca" (multi-channel authentication), "pop" (proof-of-possession).
        // Full list lives in FederatedMfaAmrValues. Case-insensitive match.
        if (HasFederatedMfa(context.User))
        {
            await next(context);
            return;
        }

        var user = await userManager.GetUserAsync(context.User);
        if (user is null)
        {
            await next(context);
            return;
        }

        var methods = await TwoFactorHelper.GetMethodsAsync(user, session);
        if (methods.Count > 0)
        {
            await next(context);
            return;
        }

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id);
        var now = DateTime.UtcNow;

        // Hard opt-out: exempt users bypass the grace check entirely. The grant of the
        // exemption itself is audited in AdminGraceEndpoints; per-request logging here
        // would just spam the audit log.
        if (securityData?.TwoFactorExempt == true)
        {
            await next(context);
            return;
        }

        // Effective grace days for this user: per-user override wins over AppSettings default.
        var effectiveGraceDays = securityData?.GracePeriodDaysOverride ?? settings.TwoFactorGracePeriodDays;

        // Lazy-stamp: a cookie issued before Level 1 was enabled will have no DueAt.
        // Fair behavior is to grant the full grace from NOW rather than block immediately.
        // Saved once; subsequent requests see DueAt populated and skip this branch.
        if (securityData?.SecureSetupDueAt is null && effectiveGraceDays > 0)
        {
            securityData ??= UserSecurityData.Create(user.Id);
            securityData.SecureSetupDueAt = now.AddDays(effectiveGraceDays);
            session.Store(securityData);
            await session.SaveChangesAsync();
            Serilog.Log.Information(
                "Grace period lazy-stamped from middleware. UserId={UserId} DueAt={DueAt}",
                user.Id, securityData.SecureSetupDueAt);
            await next(context);
            return;
        }

        if (securityData?.SecureSetupDueAt is { } due && due > now)
        {
            // Still in grace
            await next(context);
            return;
        }

        // No grace left — block.
        Serilog.Log.Warning(
            "2FA enforcement blocked request. UserId={UserId} Path={Path}",
            user.Id, path);
        ModgudMeters.RecordTwoFactorBlocked();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            Message = "2FA setup required. Grace period expired.",
            RequiresSecureSetup = true,
            GracePeriod = false,
        });
    }

    internal static bool IsWhitelisted(string path)
    {
        foreach (var prefix in AllowedPathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static readonly string[] FederatedMfaAmrValues = ["mfa", "otp", "fido", "hwk", "swk", "mca", "pop"];

    internal static bool HasFederatedMfa(System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        foreach (var claim in principal.FindAll("modgud.external.amr"))
        {
            foreach (var accepted in FederatedMfaAmrValues)
            {
                if (string.Equals(claim.Value, accepted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
