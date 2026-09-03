using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.Observability;
using Modgud.Authentication.RateLimiting;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.Api.Account;

public static class EmailOtpEndpoints
{
    public record EmailOtpLoginRequest(string Code, bool RememberMe = false);

    public static WebApplication MapEmailOtpEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/email-otp")
            .WithTags("Email OTP");

        // ═══ Profile (requires authentication) ═══

        // GET /api/account/email-otp/status — Check if Email OTP is enabled for current user
        group.MapGet("status", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            return Results.Ok(new
            {
                Enabled = user.EmailOtpEnabled,
                HasEmail = !string.IsNullOrEmpty(user.Email),
            });
        })
        .WithName("EmailOtp_Status");

        // POST /api/account/email-otp/enable — Enable Email OTP for current user
        group.MapPost("enable", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            IOAuthGrantRevoker grantRevoker,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            if (string.IsNullOrEmpty(user.Email))
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Email required",
                    detail: "An email address is required to enable Email OTP.");

            // Enabling Email-OTP makes the inbox a load-bearing security
            // factor — only allow that on an address we know the user
            // controls. Frontend mirrors this with a disabled toggle.
            if (!user.EmailConfirmed)
                return Results.Json(
                    new { error = "Email not verified", code = "Account.EmailNotVerified" },
                    statusCode: 403);

            user.EmailOtpEnabled = true;
            session.Store(user);
            await session.SaveChangesAsync(ct);

            // Audit #10/#12 — a 2FA factor change must cut off OAuth reference tokens
            // (stock introspection trusts store status). Enabling is additive, so we
            // don't force-logout other cookie sessions here (unlike disable below).
            await grantRevoker.RevokeTokensBySubjectAsync(user.Id.ToString(), ct);

            return Results.Ok(new { Message = "Email OTP enabled", Enabled = true });
        })
        .WithName("EmailOtp_Enable");

        // POST /api/account/email-otp/disable — Disable Email OTP for current user.
        // If this was the last 2FA method and enforcement is active, the user's grace
        // period is expired immediately — the next login lands on the blocking setup
        // modal without a fresh window. The user is warned in the UI before they get here.
        group.MapPost("disable", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IOAuthGrantRevoker grantRevoker,
            Modgud.Infrastructure.PositionTerminals.IStaffingRevoker staffingRevoker,
            IAuthSettings appSettings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            var willHaveZeroMethods = appSettings.AuthenticationMinimumLevel >= 1
                && (await TwoFactorHelper.GetMethodsAsync(user, session))
                    .All(m => m == "email");

            var secureSetupRequired = willHaveZeroMethods
                && await TwoFactorHelper.ExpireSetupGraceAsync(user.Id, session);

            user.EmailOtpEnabled = false;
            session.Store(user);
            await session.SaveChangesAsync(ct);

            // Audit #12 — parity with the TOTP-disable path: removing a 2FA factor
            // rotates the security stamp (other cookie sessions die at the next
            // validation pass) and re-issues the acting session so the user isn't
            // self-logged-out. Done via the UserManager AFTER the session commit so
            // it doesn't race the in-session UserSecurityData write above.
            await userManager.UpdateSecurityStampAsync(user);
            await signInManager.RefreshSignInAsync(user);

            // Audit #10 — and revoke OAuth reference tokens, which the stamp rotation
            // alone doesn't kill (stock introspection trusts store status).
            await grantRevoker.RevokeTokensBySubjectAsync(user.Id.ToString(), ct);
            await staffingRevoker.EndAllForUserAsync(
                user.Id,
                Modgud.Domain.PositionTerminals.StaffingSessionEndReason.ActivationCredentialInvalidated,
                ct);

            return Results.Ok(new
            {
                Message = "Email OTP disabled",
                Enabled = false,
                SecureSetupRequired = secureSetupRequired,
            });
        })
        .WithName("EmailOtp_Disable");

        // ═══ Login (anonymous — user is in partial sign-in state) ═══

        // POST /api/account/email-otp/login/request — Send OTP code via email
        group.MapPost("login/request", async (
            SignInManager<ApplicationUser> signInManager,
            IEmailOtpService emailOtpService,
            CancellationToken ct) =>
        {
            var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user is null)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            var result = await emailOtpService.RequestOtpAsync(user.Id, ct);
            if (result.IsError)
            {
                var error = result.FirstError;
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: error.Code,
                    detail: error.Description);
            }

            return Results.Ok(new { Message = "Verification code sent" });
        })
        .WithName("EmailOtp_LoginRequest")
        .AllowAnonymous();

        // POST /api/account/email-otp/login — Complete login with OTP code
        group.MapPost("login", async (
            EmailOtpLoginRequest request,
            HttpContext context,
            SignInManager<ApplicationUser> signInManager,
            IEmailOtpService emailOtpService,
            CancellationToken ct) =>
        {
            // Empty body / missing field — reject at the boundary instead of letting the service NRE.
            if (string.IsNullOrWhiteSpace(request?.Code))
                return Results.Json(new { Message = "Code is required" }, statusCode: 400);

            var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user is null)
            {
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.EmailOtp, ModgudMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            // Audit remediation #13: re-check account state before completing 2FA.
            // The OTP service does not gate, and the partial 2FA cookie is not
            // stamp-validated — so a user deactivated mid-login (after the partial
            // cookie was issued) could otherwise complete OTP and obtain a durable,
            // kill-switch-surviving session. Reject + clear the partial scheme.
            // (FindByIdAsync already filters IsDeleted; IsActive is the live vector.)
            if (!user.IsActive || user.IsDeleted)
            {
                await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.EmailOtp, ModgudMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            var result = await emailOtpService.VerifyOtpAsync(user.Id, request.Code, ct);
            if (result.IsError)
            {
                var error = result.FirstError;
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.EmailOtp, ModgudMeters.LoginOutcome.Failure);
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: error.Code,
                    detail: error.Description);
            }

            // Complete sign-in: set full auth cookie, clear partial cookie
            await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            await signInManager.SignInAsync(user, isPersistent: request.RememberMe);


            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.EmailOtp, ModgudMeters.LoginOutcome.Success);
            return Results.Ok(new { Message = "Login successful" });
        })
        .WithName("EmailOtp_Login")
        .AllowAnonymous()
        // Audit #24 — bound brute-force bursts against the 6-digit code; the
        // per-challenge MaxAttempts counter alone races under concurrency.
        .RequireAuthRateLimit(AuthRateLimitPolicy.EmailOtp, target: ctx => ctx.HttpContext.User.Identity?.Name);

        return application;
    }
}
