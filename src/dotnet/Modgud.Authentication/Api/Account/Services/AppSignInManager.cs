using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Modgud.Authentication.Domain;

namespace Modgud.Authentication.Api.Account.Services;

/// <summary>
/// Custom SignInManager that extends 2FA check to include Email OTP.
/// When a user has EmailOtpEnabled and an email address,
/// IsTwoFactorEnabledAsync returns true — even if TOTP is not set up.
/// This ensures PasswordSignInAsync returns RequiresTwoFactor for those users.
/// </summary>
public class AppSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    IDocumentSession session,
    Modgud.Authentication.Devices.IDeviceTrust deviceTrust)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> IsTwoFactorEnabledAsync(ApplicationUser user)
    {
        if (await base.IsTwoFactorEnabledAsync(user))
            return true;

        // Email OTP: per-user enabled + user has email
        return user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email);
    }

    // Lowest-level sign-in hook: every login path (password, MFA, magic link,
    // passkey, email OTP, external) funnels through here before the auth cookie
    // is issued. We use it to audit successful logins of 2FA-exempt users —
    // once per session instead of once per request.
    public override async Task SignInWithClaimsAsync(
        ApplicationUser user,
        AuthenticationProperties? authenticationProperties,
        IEnumerable<Claim> additionalClaims)
    {
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id);
        if (securityData?.TwoFactorExempt == true)
        {
            Serilog.Log.Warning(
                "2FA-exempt user signed in. UserId={UserId}",
                user.Id);
        }

        await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);

        // ADR 0008 — every completed interactive login (password, MFA, passkey, magic
        // link, e-mail OTP, external) marks this browser as a trusted device for the
        // user. Best-effort: the auth cookie is already issued, a failed device write
        // must not turn a successful login into a 500.
        if (Context is { } http)
        {
            try
            {
                await deviceTrust.IssueAsync(http, user.Id, http.RequestAborted);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Device cookie could not be issued for user {UserId}", user.Id);
            }
        }
    }
}
