using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authentication.Api.Account.Services;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Identity;

namespace Cocoar.Auth.Authentication.Api.Account;

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
            IDocumentSession session) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            if (string.IsNullOrEmpty(user.Email))
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Email required",
                    detail: "An email address is required to enable Email OTP.");

            user.EmailOtpEnabled = true;
            session.Store(user);
            await session.SaveChangesAsync();

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
            IAuthSettings appSettings,
            IDocumentSession session) =>
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
            await session.SaveChangesAsync();

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
            var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user is null)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            var result = await emailOtpService.VerifyOtpAsync(user.Id, request.Code, ct);
            if (result.IsError)
            {
                var error = result.FirstError;
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: error.Code,
                    detail: error.Description);
            }

            // Complete sign-in: set full auth cookie, clear partial cookie
            await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            await signInManager.SignInAsync(user, isPersistent: request.RememberMe);

            return Results.Ok(new { Message = "Login successful" });
        })
        .WithName("EmailOtp_Login")
        .AllowAnonymous();

        return application;
    }
}
