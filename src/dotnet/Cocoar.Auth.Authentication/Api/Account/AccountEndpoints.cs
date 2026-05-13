using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Serilog;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Api.Account.Services;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using BuildingBlocks.Helper;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Sessions;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Services;
using Cocoar.Auth.Infrastructure.Observability;

namespace Cocoar.Auth.Authentication.Api.Account;

public static class AccountEndpoints
{
    public record LoginRequest(string UserName, string Password, bool RememberMe = false);

    public record LogoutRequest(bool EndIdpSession = true);

    public record MeResponse(
        string Id,
        string? UserName,
        string? Acronym,
        string? Firstname,
        string? Lastname,
        string? Email,
        List<string> Permissions,
        bool Has2FA,
        List<string> TwoFactorMethods,
        DateTime? SecureSetupDueAt,
        bool TwoFactorExempt,
        /// <summary>
        /// True when the current session was established via an external IdP
        /// that asserted multi-factor authentication (amr contains "mfa", "otp",
        /// "fido", etc.). Used by the frontend to suppress the SecureSetupModal
        /// for federated sessions — the enforcement middleware already treats
        /// these as meeting the 2FA requirement.
        /// </summary>
        bool IsFederatedMfa,
        /// <summary>
        /// True whenever the current session came from an external IdP (regardless
        /// of MFA). Drives the logout-confirm dialog — only federated users get
        /// the "End IdP session too?" choice.
        /// </summary>
        bool IsFederated,
        /// <summary>
        /// Display name of the IdP the session came from (e.g. "Entra ID"), or
        /// null for local sessions. Used to label the "Sign out everywhere" button.
        /// </summary>
        string? IdpDisplayName);

    public static WebApplication MapAccountEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account")
            .WithTags("Account");

        group.MapPost("login", async (
            LoginRequest request,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IAuthSettings appSettings,
            IDocumentSession docSession,
            IQuerySession session,
            ISessionService sessionService,
            HttpContext context) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Empty body / missing fields would otherwise fall through to
            // UserManager.FindByNameAsync(null) and surface as 500. Reject
            // at the boundary with 400 instead.
            if (string.IsNullOrWhiteSpace(request?.UserName) || string.IsNullOrWhiteSpace(request?.Password))
                return Results.Json(new { Message = "UserName and Password are required" }, statusCode: 400);

            // Level 2 (Passwordless): password login disabled entirely
            if (appSettings.AuthenticationMinimumLevel >= 2)
                return Results.Json(new { Message = "Password login is disabled" }, statusCode: 403);

            // Try to find user by UserName first, then by Email
            var user = await userManager.FindByNameAsync(request.UserName);
            if (user is null)
            {
                // Try email lookup
                user = await userManager.FindByEmailAsync(request.UserName);
            }

            // All failure cases return the same 401 to prevent user enumeration.
            // Never reveal whether a username exists, is deactivated, or is locked.
            if (user is null || !user.IsActive)
            {
                Log.Warning("Auth: Login failed — user not found or inactive. UserName={UserName} IP={IP}", request.UserName, ip);
                CocoarAuthMeters.RecordLogin(CocoarAuthMeters.LoginMethod.Password, CocoarAuthMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            var result = await signInManager.PasswordSignInAsync(user, request.Password,
                isPersistent: request.RememberMe, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                Log.Information("Auth: Login successful. User={UserName} IP={IP}", user.UserName, ip);
                CocoarAuthMeters.RecordLogin(CocoarAuthMeters.LoginMethod.Password, CocoarAuthMeters.LoginOutcome.Success);

                // Track per-user device session (best-effort).
                await SessionTracker.RecordLoginAsync(sessionService, context, user.Id);

                // Level >= 1: check if user needs to set up a secure login method
                if (appSettings.AuthenticationMinimumLevel >= 1)
                {
                    var methods = await TwoFactorHelper.GetMethodsAsync(user, session);
                    if (methods.Count == 0)
                    {
                        var securityData = await docSession.LoadAsync<UserSecurityData>(user.Id);
                        if (securityData is null)
                        {
                            // EventSourcedUserStore creates UserSecurityData on first password change.
                            // Fall back to blocking setup if the document is missing — caller can
                            // set up 2FA which will create the document.
                            Log.Information("Auth: User requires secure setup (no security data). User={UserName} IP={IP}", user.UserName, ip);
                            return Results.Ok(new { RequiresSecureSetup = true, GracePeriod = false });
                        }

                        // Hard opt-out: treat as if 2FA is set up. Audit-log every occurrence.
                        if (securityData.TwoFactorExempt)
                        {
                            Log.Warning("Auth: 2FA-exempt login. User={UserName} IP={IP}", user.UserName, ip);
                            return Results.Ok(new { Message = "Login successful" });
                        }

                        // Grace period: users without 2FA get TwoFactorGracePeriodDays (or their
                        // per-user override) after their first post-enforcement login to set one
                        // up. The due date is stamped on first trigger and persists across logins.
                        var graceDays = Math.Max(0, securityData.GracePeriodDaysOverride ?? appSettings.TwoFactorGracePeriodDays);

                        if (graceDays > 0 && securityData.SecureSetupDueAt is null)
                        {
                            securityData.SecureSetupDueAt = DateTime.UtcNow.AddDays(graceDays);
                            docSession.Store(securityData);
                            await docSession.SaveChangesAsync();
                            Log.Information("Auth: Grace period started. User={UserName} DueAt={DueAt} IP={IP}",
                                user.UserName, securityData.SecureSetupDueAt, ip);
                        }

                        var inGrace = securityData.SecureSetupDueAt is { } due && due > DateTime.UtcNow;
                        Log.Information("Auth: User requires secure setup. User={UserName} InGrace={InGrace} DueAt={DueAt} IP={IP}",
                            user.UserName, inGrace, securityData.SecureSetupDueAt, ip);
                        return Results.Ok(new
                        {
                            RequiresSecureSetup = true,
                            GracePeriod = inGrace,
                            SecureSetupDueAt = securityData.SecureSetupDueAt,
                        });
                    }
                }

                return Results.Ok(new { Message = "Login successful" });
            }

            if (result.RequiresTwoFactor)
            {
                Log.Information("Auth: Login requires MFA. User={UserName} IP={IP}", user.UserName, ip);
                CocoarAuthMeters.RecordLogin(CocoarAuthMeters.LoginMethod.Password, CocoarAuthMeters.LoginOutcome.TwoFactorRequired);
                var mfaMethods = new List<string>();
                if (user.TwoFactorEnabled) mfaMethods.Add("totp");
                if (user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email)) mfaMethods.Add("email");
                return Results.Ok(new { RequiresMfa = true, MfaMethods = mfaMethods });
            }

            if (result.IsLockedOut)
            {
                Log.Warning("Auth: Login failed — account locked. User={UserName} IP={IP}", user.UserName, ip);
                CocoarAuthMeters.RecordLogin(CocoarAuthMeters.LoginMethod.Password, CocoarAuthMeters.LoginOutcome.Locked);
            }
            else
            {
                Log.Warning("Auth: Login failed — wrong password. User={UserName} IP={IP}", user.UserName, ip);
                CocoarAuthMeters.RecordLogin(CocoarAuthMeters.LoginMethod.Password, CocoarAuthMeters.LoginOutcome.Failure);
            }

            return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
        })
        .WithName("Account_Login")
        .AllowAnonymous();

        group.MapPost("logout", [Authorize] async (
            HttpContext context,
            SignInManager<ApplicationUser> signInManager,
            LogoutRequest? request) =>
        {
            // Capture the provider the session came from BEFORE signing out —
            // we'll use it to build the IdP-side logout URL for the client.
            var externalLoginProviderId = context.User.FindFirst("cocoar.external.loginProviderId")?.Value;

            await signInManager.SignOutAsync();

            // Only hand back the RP-initiated logout URL if the caller wants
            // to end the IdP session too. Default (no body) keeps the existing
            // "end everything" behavior for backwards compatibility.
            var endIdpSession = request?.EndIdpSession ?? true;
            string? externalLogoutUrl = null;
            if (endIdpSession && !string.IsNullOrWhiteSpace(externalLoginProviderId))
            {
                externalLogoutUrl = $"/api/account/external-logout/{externalLoginProviderId}";
            }

            return Results.Ok(new { Message = "Logout successful", ExternalLogoutUrl = externalLogoutUrl });
        })
        .WithName("Account_Logout");

        group.MapGet("me", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            IPermissionService permissionService,
            IQuerySession session) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            // /me reports the current user's permissions in the two Apps the
            // SPA itself uses for gating: cocoar-auth (admin surface, the
            // bulk of the UI) and control-plane (cross-realm management when
            // the SPA is loaded on the Control-Plane host). Both are merged
            // into a single bare-2-segment list — the SPA's hasPermission
            // doesn't carry an App context. External apps fetch their own
            // scoped permissions via the distribution API instead.
            var cocoarAuthPerms = await permissionService.GetUserPermissionsAsync(user.Id, AppSlugs.CocoarAuth);
            var controlPlanePerms = await permissionService.GetUserPermissionsAsync(user.Id, AppSlugs.ControlPlane);
            var permissions = cocoarAuthPerms.Union(controlPlanePerms).ToList();
            var twoFactorMethods = await TwoFactorHelper.GetMethodsAsync(user, session);
            var securityData = await session.LoadAsync<UserSecurityData>(user.Id);

            var isFederatedMfa = context.User.FindAll("timetodo.external.amr")
                .Any(c => c.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("otp", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("fido", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("hwk", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("swk", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("mca", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("pop", StringComparison.OrdinalIgnoreCase));

            var externalLoginProviderIdRaw = context.User.FindFirst("cocoar.external.loginProviderId")?.Value;
            var isFederated = !string.IsNullOrWhiteSpace(externalLoginProviderIdRaw);
            string? idpDisplayName = null;
            if (isFederated && Guid.TryParse(externalLoginProviderIdRaw, out var loginProviderId))
            {
                var loginProvider = await session.LoadAsync<LoginProvider>(loginProviderId);
                idpDisplayName = loginProvider?.DisplayName;
            }

            return Results.Ok(new MeResponse(
                new ShortGuid(user.Id).ToString(),
                user.UserName,
                user.Acronym,
                user.Firstname,
                user.Lastname,
                user.Email,
                permissions,
                twoFactorMethods.Count > 0,
                twoFactorMethods,
                securityData?.SecureSetupDueAt,
                securityData?.TwoFactorExempt ?? false,
                isFederatedMfa,
                isFederated,
                idpDisplayName));
        })
        .WithName("Account_Me");

        group.MapPost("change-password", [Authorize] async (
            ChangePasswordRequest request,
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            IAuthSettings appSettings) =>
        {
            if (appSettings.AuthenticationMinimumLevel >= 2)
                return Results.Json(new { Message = "Password operations are disabled" }, statusCode: 403);

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                Log.Warning("Auth: Change password failed. User={UserName} IP={IP}", user.UserName, ip);
                var errors = result.Errors.Select(e => e.Description).ToList();
                return Results.Json(new { Message = string.Join(" ", errors) }, statusCode: 400);
            }

            Log.Information("Auth: Password changed. User={UserName} IP={IP}", user.UserName, ip);
            return Results.Ok(new { Message = "Password changed successfully" });
        })
        .WithName("Account_ChangePassword");

        return application;
    }

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}
