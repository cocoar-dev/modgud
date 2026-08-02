using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Serilog;
using Modgud.Authentication;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.LoginProviders;
using BuildingBlocks.Helper;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Observability;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Authentication.Api.Account;

public static class AccountEndpoints
{
    public record LoginRequest(
        string UserName,
        string Password,
        bool RememberMe = false,
        string? ReturnUrl = null);

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
        string? IdpDisplayName,
        /// <summary>
        /// Identity-side EmailConfirmed flag. Drives the in-app
        /// unverified-email banner and gates self-service forgot-password /
        /// magic-link on the backend.
        /// </summary>
        bool EmailConfirmed);

    public static WebApplication MapAccountEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account")
            .WithTags("Account");

        group.MapPost("login", async (
            LoginRequest request,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IAuthSettings appSettings,
            IApplicationSettingsResolver applicationSettings,
            IDocumentSession docSession,
            IQuerySession session,
            ISecurityAuditLog securityAudit,
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
            var clientId = ExternalAuth.ExternalAuthEndpoints.ExtractAuthorizeClientId(request.ReturnUrl);
            if ((await applicationSettings.ResolveForRequestAsync(context, clientId, context.RequestAborted))
                .LoginExperience?.InternalLoginEnabled == false)
                return Results.Json(new { Message = "Internal login is disabled for this application" }, statusCode: 403);

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
                // Audit M3: equalize response time so an attacker cannot tell an
                // unknown/inactive account (no hashing) from a valid one (PBKDF2)
                // by latency. Burn an equivalent hash verify before the 401.
                PasswordTimingSafety.EqualizeFailure(userManager.PasswordHasher, request.Password);

                securityAudit.RecordAbuse(new SecurityAuditRecord
                {
                    EventType = AuditEvents.LoginFailedUnknownUser,
                    Severity = AuditSeverity.Warning,
                    ActorKind = AuditActorKind.AnonymousIdentifier,
                    TargetSubjectId = user?.Id,
                    UnknownIdentifier = user is null ? request.UserName : null,
                    IpAddress = ip,
                    AuthenticationMethod = ModgudMeters.LoginMethod.Password,
                    OutcomeCode = AuditOutcomes.Rejected,
                    ReasonCode = user is null ? "user-not-found" : "user-inactive",
                });
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Password, ModgudMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            var result = await signInManager.PasswordSignInAsync(user, request.Password,
                isPersistent: request.RememberMe, lockoutOnFailure: true);

            // Audit M4: when the realm requires verified emails, an unverified
            // account must not complete login (even though it may be active).
            // Checked AFTER the password is validated — uniform timing, and an
            // attacker without the password still only sees "invalid credentials".
            // Clear whatever cookie / partial-2FA state PasswordSignInAsync just
            // issued so no session survives the block.
            if (result.Succeeded || result.RequiresTwoFactor)
            {
                var realmSettings = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId);
                if (realmSettings?.SelfRegistration?.RequireEmailVerification == true && !user.EmailConfirmed)
                {
                    await signInManager.SignOutAsync();
                    securityAudit.RecordAbuse(new SecurityAuditRecord
                    {
                        EventType = AuditEvents.LoginFailed,
                        Severity = AuditSeverity.Warning,
                        TargetSubjectId = user.Id,
                        IpAddress = ip,
                        AuthenticationMethod = ModgudMeters.LoginMethod.Password,
                        OutcomeCode = AuditOutcomes.Rejected,
                        ReasonCode = "email-not-verified",
                    });
                    ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Password, ModgudMeters.LoginOutcome.Failure);
                    return Results.Json(new
                    {
                        Message = "Please verify your email address before signing in.",
                        Code = "Account.EmailNotVerified",
                    }, statusCode: 403);
                }
            }

            if (result.Succeeded)
            {
                Log.Information("Login successful. UserId={UserId} IP={IP}", user.Id, ip);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Password, ModgudMeters.LoginOutcome.Success);

                // Audit marker on the user's stream (Phase 1): the "when + by what
                // method" of a successful login. No IP on the event — the authoritative
                // browser session created by the cookie event owns IP/device metadata.
                // Erasable with the user. Best-effort: PasswordSignInAsync has already
                // issued the auth cookie, so a failed marker write must NOT turn a
                // successful login into a 500 — log and continue.
                try
                {
                    docSession.Events.Append(user.Id, new Modgud.Authentication.Events.UserLoggedInEvent(
                        user.Id, IpAddress: null, Method: ModgudMeters.LoginMethod.Password));
                    await docSession.SaveChangesAsync(context.RequestAborted);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "failed to persist login audit marker for user {UserId}", user.Id);
                }

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
                            Log.Information("User requires secure setup (no security data). UserId={UserId} IP={IP}", user.Id, ip);
                            return Results.Ok(new { RequiresSecureSetup = true, GracePeriod = false });
                        }

                        // Hard opt-out: treat as if 2FA is set up. Audit-log every occurrence.
                        if (securityData.TwoFactorExempt)
                        {
                            Log.Warning("2FA-exempt login. UserId={UserId} IP={IP}", user.Id, ip);
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
                            Log.Information("Grace period started. UserId={UserId} DueAt={DueAt} IP={IP}",
                                user.Id, securityData.SecureSetupDueAt, ip);
                        }

                        var inGrace = securityData.SecureSetupDueAt is { } due && due > DateTime.UtcNow;
                        Log.Information("User requires secure setup. UserId={UserId} InGrace={InGrace} DueAt={DueAt} IP={IP}",
                            user.Id, inGrace, securityData.SecureSetupDueAt, ip);
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
                Log.Information("Login requires MFA. UserId={UserId} IP={IP}", user.Id, ip);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Password, ModgudMeters.LoginOutcome.TwoFactorRequired);
                var mfaMethods = new List<string>();
                if (user.TwoFactorEnabled) mfaMethods.Add("totp");
                if (user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email)) mfaMethods.Add("email");
                return Results.Ok(new { RequiresMfa = true, MfaMethods = mfaMethods });
            }

            if (result.IsLockedOut)
            {
                Log.Warning("Login failed — account locked. UserId={UserId} IP={IP}", user.Id, ip);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Password, ModgudMeters.LoginOutcome.Locked);
            }
            else
            {
                Log.Warning("Login failed — wrong password. UserId={UserId} IP={IP}", user.Id, ip);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Password, ModgudMeters.LoginOutcome.Failure);
            }

            return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
        })
        .WithName("Account_Login")
        .AllowAnonymous();

        group.MapPost("logout", [Authorize] async (
            HttpContext context,
            SignInManager<ApplicationUser> signInManager,
            IQuerySession session,
            LogoutRequest? request) =>
        {
            // Capture the provider the session came from BEFORE signing out —
            // an upstream logout exists only for OIDC. SAML is SP-initiated
            // login only in v1 and has no Single Logout endpoint.
            var externalLoginProviderIdRaw =
                context.User.FindFirst("modgud.external.loginProviderId")?.Value;
            var endIdpSession = request?.EndIdpSession ?? true;
            LoginProvider? externalLoginProvider = null;

            if (endIdpSession
                && Guid.TryParse(externalLoginProviderIdRaw, out var externalLoginProviderId))
            {
                try
                {
                    externalLoginProvider =
                        await session.LoadAsync<LoginProvider>(externalLoginProviderId);
                }
                catch (Exception ex)
                {
                    // Upstream logout is optional. A provider lookup failure
                    // must never prevent the authoritative local logout.
                    Log.Warning(
                        ex,
                        "Could not resolve external login provider {LoginProviderId} during logout; continuing with local logout",
                        externalLoginProviderId);
                }
            }

            await signInManager.SignOutAsync();

            // Disabled/deleted providers no longer have a registered OIDC
            // scheme, so they intentionally degrade to local logout too.
            var externalLogoutUrl =
                externalLoginProvider is
                {
                    Type: LoginProviderType.Oidc,
                    Enabled: true,
                    IsDeleted: false,
                }
                    ? $"/api/account/external-logout/{externalLoginProvider.Id}"
                    : null;

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
            // SPA itself uses for gating: modgud (admin surface, the
            // bulk of the UI) and control-plane (cross-realm management when
            // the SPA is loaded on the Control-Plane host). Both are merged
            // into a single bare-2-segment list — the SPA's hasPermission
            // doesn't carry an App context. External apps fetch their own
            // scoped permissions via the distribution API instead.
            var cocoarAuthPerms = await permissionService.GetUserPermissionsAsync(user.Id, AppSlugs.Modgud);
            var controlPlanePerms = await permissionService.GetUserPermissionsAsync(user.Id, AppSlugs.ControlPlane);
            var permissions = cocoarAuthPerms.Union(controlPlanePerms).ToList();
            var twoFactorMethods = await TwoFactorHelper.GetMethodsAsync(user, session);
            var securityData = await session.LoadAsync<UserSecurityData>(user.Id);

            var isFederatedMfa = context.User.FindAll("modgud.external.amr")
                .Any(c => c.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("otp", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("fido", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("hwk", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("swk", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("mca", StringComparison.OrdinalIgnoreCase)
                       || c.Value.Equals("pop", StringComparison.OrdinalIgnoreCase));

            var externalLoginProviderIdRaw = context.User.FindFirst("modgud.external.loginProviderId")?.Value;
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
                idpDisplayName,
                user.EmailConfirmed));
        })
        .WithName("Account_Me");

        group.MapPost("change-password", [Authorize] async (
            ChangePasswordRequest request,
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IUserAccessRevoker accessRevoker,
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
                Log.Warning("Change password failed. UserId={UserId} IP={IP}", user.Id, ip);
                var errors = result.Errors.Select(e => e.Description).ToList();
                return Results.Json(new { Message = string.Join(" ", errors) }, statusCode: 400);
            }

            // Audit L5: a password change must immediately revoke the user's OTHER
            // live access — already-issued OAuth tokens and every other device
            // session — not merely rely on the <=5-min security-stamp window. Kill
            // everything, then refresh the CURRENT session (reload the user first so
            // it carries the freshly-rotated stamp) so the password-changer stays
            // signed in here. BrowserSessionCookieEvents creates the replacement
            // authoritative row as part of the refreshed cookie.
            await accessRevoker.RevokeAllAccessAsync(
                user.Id, AccessRevocationReason.ForceSignOut, context.RequestAborted);
            var refreshed = await userManager.FindByIdAsync(user.Id.ToString());
            if (refreshed is not null)
            {
                await signInManager.RefreshSignInAsync(refreshed);
            }

            Log.Information("Password changed; other sessions revoked. UserId={UserId} IP={IP}", user.Id, ip);
            return Results.Ok(new { Message = "Password changed successfully" });
        })
        .WithName("Account_ChangePassword");

        return application;
    }

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}
