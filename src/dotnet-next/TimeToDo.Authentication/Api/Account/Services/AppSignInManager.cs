using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TimeToDo.Authentication.Domain;

namespace TimeToDo.Authentication.Api.Account.Services;

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
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> IsTwoFactorEnabledAsync(ApplicationUser user)
    {
        if (await base.IsTwoFactorEnabledAsync(user))
            return true;

        // Email OTP: per-user enabled + user has email
        return user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email);
    }
}
