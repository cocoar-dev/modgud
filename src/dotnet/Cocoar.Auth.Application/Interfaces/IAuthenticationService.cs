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
