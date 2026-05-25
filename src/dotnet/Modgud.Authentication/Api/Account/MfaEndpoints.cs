using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Observability;

namespace Modgud.Authentication.Api.Account;

public static class MfaEndpoints
{
    public record MfaSetupResponse(string SharedKey, string AuthenticatorUri);
    public record MfaVerifyRequest(string Code);
    public record MfaLoginRequest(string Code, bool RememberMe = false, bool RememberMachine = false);

    private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";
    private const string Issuer = "Modgud";

    public static WebApplication MapMfaEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/mfa")
            .WithTags("MFA")
            .RequireAuthorization();

        // GET /api/account/mfa/status — Check if MFA is enabled for current user
        group.MapGet("status", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            return Results.Ok(new
            {
                Enabled = await userManager.GetTwoFactorEnabledAsync(user),
                HasAuthenticator = await userManager.GetAuthenticatorKeyAsync(user) is not null,
            });
        })
        .WithName("Mfa_Status");

        // POST /api/account/mfa/setup — Generate authenticator key + QR code URI
        group.MapPost("setup", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            // Reset the authenticator key (generates a new one if needed)
            await userManager.ResetAuthenticatorKeyAsync(user);
            var unformattedKey = await userManager.GetAuthenticatorKeyAsync(user);

            if (string.IsNullOrEmpty(unformattedKey))
                return Results.Problem("Failed to generate authenticator key.");

            var sharedKey = FormatKey(unformattedKey);
            var authenticatorUri = GenerateQrCodeUri(user.UserName ?? user.Acronym ?? "user", unformattedKey);

            return Results.Ok(new MfaSetupResponse(sharedKey, authenticatorUri));
        })
        .WithName("Mfa_Setup");

        // POST /api/account/mfa/verify — Verify TOTP code and enable MFA
        group.MapPost("verify", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            MfaVerifyRequest request) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            // Verify the code against the stored authenticator key
            var isValid = await userManager.VerifyTwoFactorTokenAsync(
                user,
                userManager.Options.Tokens.AuthenticatorTokenProvider,
                request.Code.Replace(" ", "").Replace("-", ""));

            if (!isValid)
                return Results.Json(new { Message = "Invalid verification code." }, statusCode: 400);

            // Enable 2FA
            await userManager.SetTwoFactorEnabledAsync(user, true);

            return Results.Ok(new { Message = "MFA has been enabled.", Enabled = true });
        })
        .WithName("Mfa_Verify");

        // POST /api/account/mfa/disable — Disable MFA (requires authentication)
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
                    .All(m => m == "totp");

            // Exempt users skip enforcement entirely — ExpireSetupGraceAsync returns
            // false in that case so we don't tell the UI to force re-auth.
            var secureSetupRequired = willHaveZeroMethods
                && await TwoFactorHelper.ExpireSetupGraceAsync(user.Id, session);

            await userManager.SetTwoFactorEnabledAsync(user, false);
            await userManager.ResetAuthenticatorKeyAsync(user);

            return Results.Ok(new
            {
                Message = "MFA has been disabled.",
                Enabled = false,
                SecureSetupRequired = secureSetupRequired,
            });
        })
        .WithName("Mfa_Disable");

        // POST /api/account/mfa/login — Complete login with TOTP code
        // This endpoint is anonymous because the user hasn't fully signed in yet
        // (they passed password check but need to provide the 2FA code).
        // ASP.NET Identity stores a partial sign-in cookie to track this state.
        group.MapPost("login", async (
            SignInManager<ApplicationUser> signInManager,
            MfaLoginRequest request,
            ISessionService sessionService,
            HttpContext context) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Empty body / missing field would NRE on request.Code below — reject at the boundary.
            if (string.IsNullOrWhiteSpace(request?.Code))
                return Results.Json(new { Message = "Code is required" }, statusCode: 400);

            var code = request.Code.Replace(" ", "").Replace("-", "");

            // Capture the user that's mid-2FA *before* we sign in — afterwards the
            // partial sign-in cookie is gone and we lose the handle.
            var twoFactorUser = await signInManager.GetTwoFactorAuthenticationUserAsync();

            var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
                code, isPersistent: request.RememberMe, rememberClient: request.RememberMachine);

            if (result.Succeeded)
            {
                if (twoFactorUser is not null)
                    await SessionTracker.RecordLoginAsync(sessionService, context, twoFactorUser.Id);

                Serilog.Log.Information("Auth: MFA login successful. IP={IP}", ip);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Mfa, ModgudMeters.LoginOutcome.Success);
                return Results.Ok(new { Message = "Login successful" });
            }

            Serilog.Log.Warning("Auth: MFA login failed — invalid code. IP={IP} Locked={Locked}", ip, result.IsLockedOut);

            if (result.IsLockedOut)
            {
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Mfa, ModgudMeters.LoginOutcome.Locked);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Mfa, ModgudMeters.LoginOutcome.Failure);
            return Results.Json(new { Message = "Invalid authenticator code." }, statusCode: 401);
        })
        .WithName("Mfa_Login")
        .AllowAnonymous(); // Must be anonymous — user is in partial sign-in state

        return application;
    }

    /// <summary>
    /// Format the unformatted key into groups of 4 for easier reading.
    /// </summary>
    private static string FormatKey(string unformattedKey)
    {
        var sb = new StringBuilder();
        var currentPosition = 0;
        while (currentPosition + 4 < unformattedKey.Length)
        {
            sb.Append(unformattedKey.AsSpan(currentPosition, 4)).Append(' ');
            currentPosition += 4;
        }
        if (currentPosition < unformattedKey.Length)
            sb.Append(unformattedKey.AsSpan(currentPosition));
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Generate the otpauth:// URI for QR code scanning.
    /// </summary>
    private static string GenerateQrCodeUri(string userName, string unformattedKey)
    {
        return string.Format(
            AuthenticatorUriFormat,
            UrlEncoder.Default.Encode(Issuer),
            UrlEncoder.Default.Encode(userName),
            unformattedKey);
    }
}
