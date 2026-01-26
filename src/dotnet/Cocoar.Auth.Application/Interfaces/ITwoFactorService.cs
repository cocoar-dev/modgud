using Cocoar.Auth.Application.DTOs.Auth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for managing two-factor authentication.
/// </summary>
public interface ITwoFactorService
{
    /// <summary>
    /// Gets the current 2FA status for a user.
    /// </summary>
    Task<ErrorOr<TwoFactorStatusDto>> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a new authenticator key and returns the setup information.
    /// </summary>
    Task<ErrorOr<TwoFactorSetupDto>> GenerateSetupAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the TOTP code and enables 2FA for the user.
    /// </summary>
    Task<ErrorOr<bool>> EnableAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the TOTP code and disables 2FA for the user.
    /// </summary>
    Task<ErrorOr<bool>> DisableAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates new recovery codes for the user.
    /// </summary>
    Task<ErrorOr<RecoveryCodesDto>> GenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a recovery code and marks it as used.
    /// </summary>
    Task<ErrorOr<bool>> ValidateRecoveryCodeAsync(Guid userId, string code, CancellationToken cancellationToken = default);
}
