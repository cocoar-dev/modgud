using Cocoar.Auth.Domain.Entities;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Interface for authentication operations.
/// This abstracts away ASP.NET Core specific SignInManager.
/// </summary>
public interface IAuthenticationService
{
    Task<SignInResultInfo> PasswordSignInAsync(
        ApplicationUser user,
        string password,
        bool isPersistent,
        bool lockoutOnFailure,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in a user with a two-factor authentication code.
    /// </summary>
    Task<SignInResultInfo> TwoFactorSignInAsync(
        string code,
        bool isPersistent,
        bool rememberClient,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in a user with a recovery code.
    /// </summary>
    Task<SignInResultInfo> RecoveryCodeSignInAsync(
        string recoveryCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user that is awaiting two-factor authentication.
    /// </summary>
    Task<ApplicationUser?> GetTwoFactorAuthenticationUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in a user directly (after successful 2FA verification).
    /// </summary>
    Task SignInAsync(
        ApplicationUser user,
        bool isPersistent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in a user by their ID (for passwordless flows).
    /// </summary>
    Task SignInByIdAsync(
        Guid userId,
        bool isPersistent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the user for two-factor authentication (sets the 2FA cookie).
    /// Used when external login detects that the user has 2FA enabled.
    /// </summary>
    Task StoreTwoFactorUserAsync(
        ApplicationUser user,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a sign-in attempt.
/// </summary>
public record SignInResultInfo
{
    public bool Succeeded { get; init; }
    public bool IsLockedOut { get; init; }
    public bool IsNotAllowed { get; init; }
    public bool RequiresTwoFactor { get; init; }
}
