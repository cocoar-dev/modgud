using Cocoar.Auth.Application.DTOs.Auth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for managing email-based OTP two-factor authentication.
/// </summary>
public interface IEmailOtpService
{
    /// <summary>
    /// Requests a new OTP code to be sent to the user's email.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="ipAddress">The IP address of the request (for audit).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<ErrorOr<bool>> RequestOtpAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an OTP code.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="code">The OTP code to verify.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current OTP status for a user.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The OTP status.</returns>
    Task<ErrorOr<EmailOtpStatusDto>> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any pending OTP challenge for a user.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearChallengeAsync(Guid userId, CancellationToken cancellationToken = default);
}
