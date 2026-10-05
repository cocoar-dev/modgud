using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.SignIn;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Observability;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// Server-side enforcement of the sign-in level for Modgud's own UI (ADR 0025). The frontend
/// is never the enforcement boundary — a curl, an old tab, or a modified client must also be
/// blocked.
///
/// The target of a request is the realm's administration for <c>/api/admin/*</c> (its
/// administration minimum level) and the self-service portal — the <c>modgud</c> App —
/// for everything else. A session below the target's level gets 403 with
/// <c>RequiresStepUp</c> when the user has a second factor to raise it with, or with
/// <c>RequiresSecureSetup</c> once the setup grace for a missing second factor is over.
///
/// Setup and sign-in endpoints are whitelisted so a user can enroll a factor, step up,
/// check their identity, or log out.
/// </summary>
public class TwoFactorEnforcementMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Paths callable below the target's level. All start with "/api/account/" — the account
    /// feature area is what lets a user recover without leaving the login screen. Matched
    /// case-insensitively via StartsWith so "/api/account/mfa/setup" passes "/api/account/mfa/".
    /// </summary>
    private static readonly string[] AllowedPathPrefixes =
    [
        "/api/account/me",
        "/api/account/logout",
        "/api/account/step-up",
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
        UserManager<ApplicationUser> userManager,
        ISignInRequirementService signInRequirements)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Anonymous endpoints (app-info, login, magic-link request, forgot-password, health, …)
        // must stay reachable even if the caller's cookie is below the level. Otherwise
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

        // A multi-factor session meets every requirement — no lookups on the hot path.
        // A federated sign-in whose provider asserted MFA records external_mfa (ADR 0025).
        var factors = SignInAssurance.ReadFactors(context.User);
        if (SignInAssurance.LevelOf(factors.Keys) >= SignInLevel.Multi)
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

        var target = path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)
            ? await signInRequirements.AdministrationTargetAsync(context.RequestAborted)
            : await signInRequirements.ResolveTargetFromReturnUrlAsync(returnUrl: null, context.RequestAborted);
        var decision = await signInRequirements.EvaluateAsync(
            user, target, factors, SignInSurface.Web, startSetupGrace: true, context.RequestAborted);

        if (decision.IsSatisfied)
        {
            await next(context);
            return;
        }

        Serilog.Log.Warning(
            "Sign-in level enforcement blocked request. UserId={UserId} Path={Path} Outcome={Outcome}",
            user.Id, path, decision.Outcome);
        ModgudMeters.RecordTwoFactorBlocked();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        if (decision.Outcome == SignInOutcome.NeedSecondFactor)
        {
            await context.Response.WriteAsJsonAsync(new
            {
                Message = "This area requires a second factor for the current session.",
                RequiresStepUp = true,
            });
            return;
        }
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
