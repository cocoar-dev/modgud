using Cocoar.Auth.Application.DTOs.Auth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for GDPR compliance operations including data export,
/// account deletion, and data masking.
/// </summary>
public interface IGdprService
{
    /// <summary>
    /// Exports all user data for GDPR portability (Article 20).
    /// Returns a structured export of all user information.
    /// </summary>
    Task<ErrorOr<UserDataExportDto>> ExportUserDataAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests account deletion. This initiates a confirmation period
    /// before the actual deletion occurs.
    /// </summary>
    Task<ErrorOr<DeletionRequestDto>> RequestDeletionAsync(
        Guid userId,
        string password,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms account deletion after the user verifies via email token.
    /// This triggers the actual data masking process.
    /// </summary>
    Task<ErrorOr<bool>> ConfirmDeletionAsync(
        Guid userId,
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a pending deletion request.
    /// </summary>
    Task<ErrorOr<bool>> CancelDeletionAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current deletion status for a user.
    /// </summary>
    Task<ErrorOr<DeletionStatusDto>> GetDeletionStatusAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft deletes a user account (admin action).
    /// Marks the user as deleted but preserves data.
    /// </summary>
    Task<ErrorOr<bool>> SoftDeleteUserAsync(
        Guid userId,
        Guid adminUserId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a soft-deleted user account (admin action).
    /// </summary>
    Task<ErrorOr<bool>> RestoreUserAsync(
        Guid userId,
        Guid adminUserId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently erases user data by applying GDPR masking rules (admin action).
    /// This masks PII in the event stream and archives it.
    /// Cannot be undone.
    /// </summary>
    Task<ErrorOr<bool>> PermanentlyEraseUserDataAsync(
        Guid userId,
        Guid adminUserId,
        string reason,
        CancellationToken cancellationToken = default);
}
