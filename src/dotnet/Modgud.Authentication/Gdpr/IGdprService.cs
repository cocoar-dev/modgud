using ErrorOr;

namespace Modgud.Authentication.Gdpr;

public interface IGdprService
{
    Task<ErrorOr<UserDataExportDto>> ExportUserDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Records a self-initiated deletion request and emails the
    /// confirmation token. Requires the caller's current password.</summary>
    Task<ErrorOr<DeletionRequestResponseDto>> RequestDeletionAsync(Guid userId, string password, string? reason, CancellationToken ct = default);

    Task<ErrorOr<bool>> ConfirmDeletionAsync(Guid userId, string token, CancellationToken ct = default);

    Task<ErrorOr<bool>> CancelDeletionAsync(Guid userId, CancellationToken ct = default);

    Task<ErrorOr<DeletionStatusDto>> GetDeletionStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Admin-driven permanent erasure: applies Marten data masking,
    /// archives the event stream, deletes secondary documents.</summary>
    Task<ErrorOr<bool>> PermanentlyEraseAsync(Guid userId, Guid? adminUserId, string reason, CancellationToken ct = default);
}
