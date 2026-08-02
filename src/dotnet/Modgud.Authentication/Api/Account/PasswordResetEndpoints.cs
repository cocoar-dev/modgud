using System.Text;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Account;

public static class PasswordResetEndpoints
{
    /// <summary><paramref name="ReturnUrl"/> threads a pending post-login
    /// continuation (e.g. a client app's /connect/authorize flow the user
    /// detoured away from via "Forgot password?") through the e-mail round
    /// trip; validated as a same-origin path before it is appended to the
    /// emailed reset URL as <c>?redirect=</c>.</summary>
    public record ForgotPasswordRequest(string UserName, string? ReturnUrl = null);
    public record ResetPasswordRequest(string UserId, string Token, string NewPassword);

    public static WebApplication MapPasswordResetEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account")
            .WithTags("Password Reset")
            .AllowAnonymous();

        // POST /api/account/forgot-password — Request password reset email
        group.MapPost("forgot-password", async (
            ForgotPasswordRequest request,
            UserManager<ApplicationUser> userManager,
            IEmailService emailService,
            Modgud.Authentication.Applications.IEmailBrandingResolver emailBranding,
            IRealmProvisioningService realmSvc,
            IAuthSettings appSettings,
            IWebHostEnvironment env,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (appSettings.AuthenticationMinimumLevel >= 2)
                return Results.Json(new { Message = "Password operations are disabled" }, statusCode: 403);

            // Always return success to prevent user enumeration
            var user = await userManager.FindByNameAsync(request.UserName)
                    ?? await userManager.FindByEmailAsync(request.UserName);

            // Resolve the realm whose primary domain hosts the reset link. A
            // missing realm is exotic (RealmMiddleware would have 404'd) — fall
            // through to the constant generic response to keep enumeration silent.
            var realm = await context.ResolveCurrentRealmAsync(realmSvc, ct);

            // Gate on EmailConfirmed: sending a reset link to an unverified
            // address would route the credential-reset path to a mailbox we
            // haven't established the user controls. Generic response holds
            // either way to keep enumeration silent.
            if (realm is not null && user is not null && !string.IsNullOrEmpty(user.Email) && user.EmailConfirmed)
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var encodedToken = HttpUtility.UrlEncode(token);
                var userId = user.Id.ToString();

                // Build reset URL — Vue frontend handles /reset-password route
                var appUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, env);
                var resetUrl = $"{appUrl}/reset-password?userId={userId}&token={encodedToken}";

                // Thread the pending continuation through the e-mail round trip
                // so a password reset can resume a client app's OIDC authorize
                // flow. Same-origin guard mirrors the SPA's; the reset page
                // forwards ?redirect= to /login after a successful reset.
                if (LoginRedirectGuard.IsSameOriginPath(request.ReturnUrl) && request.ReturnUrl != "/")
                    resetUrl += $"&redirect={Uri.EscapeDataString(request.ReturnUrl!)}";

                await emailService.SendTemplatedEmailAsync(
                    user.Email,
                    EmailTemplate.PasswordReset,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                        ["ActionUrl"] = resetUrl,
                        ["ExpirationMinutes"] = "1440",
                    }, ct: ct), ct);
            }

            // Always return OK — never reveal if user/email exists
            return Results.Ok(new { Message = "Falls ein Konto mit diesem Benutzernamen existiert, wurde eine E-Mail gesendet." });
        })
        .WithName("Account_ForgotPassword")
        // RATE-01 — 5 requests per hour per IP. Bounds the enum-via-email
        // surface even though the response body is constant ("if your account
        // exists, an email is on the way") and prevents SMTP-pipeline DoS.
        .RequireRateLimiting("password-reset");

        // POST /api/account/reset-password — Reset password with token
        group.MapPost("reset-password", async (
            ResetPasswordRequest request,
            UserManager<ApplicationUser> userManager,
            Modgud.Authentication.Sessions.IUserAccessRevoker accessRevoker,
            IAuthSettings appSettings) =>
        {
            if (appSettings.AuthenticationMinimumLevel >= 2)
                return Results.Json(new { Message = "Password operations are disabled" }, statusCode: 403);

            if (!Guid.TryParse(request.UserId, out var userId))
                return Results.Json(new { Message = "Ungültiger oder abgelaufener Link." }, statusCode: 400);

            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                // Don't reveal that user doesn't exist
                return Results.Json(new { Message = "Ungültiger oder abgelaufener Link." }, statusCode: 400);

            // Token arrives already decoded from the frontend (Vue Router decodes query params)
            var result = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);

            if (!result.Succeeded)
            {
                var errors = result.Errors.Select(e => e.Description).ToList();
                // Check if it's a token error vs. password policy error
                if (errors.Any(e => e.Contains("token", StringComparison.OrdinalIgnoreCase)))
                    return Results.Json(new { Message = "Ungültiger oder abgelaufener Link." }, statusCode: 400);

                return Results.Json(new { Message = string.Join(" ", errors) }, statusCode: 400);
            }

            // Audit remediation #3: the anonymous ATO-recovery path. ResetPasswordAsync
            // rotates the Identity stamp (cuts cookies + refresh-token continuation) but
            // touches no OAuth grants and no device-session rows — leaving an attacker's
            // live reference access token + session rows alive. Revoke everything.
            // (Anonymous request → no acting session to RefreshSignIn.)
            await accessRevoker.RevokeAllAccessAsync(user.Id, Modgud.Authentication.Sessions.AccessRevocationReason.ForceSignOut);

            return Results.Ok(new { Message = "Passwort wurde erfolgreich zurückgesetzt." });
        })
        .WithName("Account_ResetPassword");

        return application;
    }
}
