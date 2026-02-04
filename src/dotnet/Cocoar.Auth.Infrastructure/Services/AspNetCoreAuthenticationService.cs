using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Implementation of IAuthenticationService using ASP.NET Core SignInManager.
/// </summary>
public class AspNetCoreAuthenticationService : IAuthenticationService
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public AspNetCoreAuthenticationService(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    public async Task<SignInResultInfo> PasswordSignInAsync(
        ApplicationUser user,
        string password,
        bool isPersistent,
        bool lockoutOnFailure,
        CancellationToken cancellationToken = default)
    {
        var result = await _signInManager.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);

        return new SignInResultInfo
        {
            Succeeded = result.Succeeded,
            IsLockedOut = result.IsLockedOut,
            IsNotAllowed = result.IsNotAllowed,
            RequiresTwoFactor = result.RequiresTwoFactor
        };
    }

    public async Task<SignInResultInfo> TwoFactorSignInAsync(
        string code,
        bool isPersistent,
        bool rememberClient,
        CancellationToken cancellationToken = default)
    {
        var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient);

        return new SignInResultInfo
        {
            Succeeded = result.Succeeded,
            IsLockedOut = result.IsLockedOut,
            IsNotAllowed = result.IsNotAllowed,
            RequiresTwoFactor = result.RequiresTwoFactor
        };
    }

    public async Task<SignInResultInfo> RecoveryCodeSignInAsync(
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(recoveryCode);

        return new SignInResultInfo
        {
            Succeeded = result.Succeeded,
            IsLockedOut = result.IsLockedOut,
            IsNotAllowed = result.IsNotAllowed,
            RequiresTwoFactor = result.RequiresTwoFactor
        };
    }

    public async Task<ApplicationUser?> GetTwoFactorAuthenticationUserAsync(CancellationToken cancellationToken = default)
    {
        return await _signInManager.GetTwoFactorAuthenticationUserAsync();
    }

    public async Task SignInAsync(
        ApplicationUser user,
        bool isPersistent,
        CancellationToken cancellationToken = default)
    {
        await _signInManager.SignInAsync(user, isPersistent);
    }

    public async Task SignInByIdAsync(
        Guid userId,
        bool isPersistent,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is not null)
        {
            await _signInManager.SignInAsync(user, isPersistent);
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _signInManager.SignOutAsync();
    }
}
