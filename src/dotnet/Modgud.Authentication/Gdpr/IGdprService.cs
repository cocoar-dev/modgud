using ErrorOr;

namespace Modgud.Authentication.Gdpr;

public interface IGdprService
{
    Task<ErrorOr<UserDataExportDto>> ExportUserDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Schedules a self-service deletion: the account is erased at the
    /// end of the realm's grace window unless the user logs in and cancels.
    /// The user stays active during grace. Requires the caller's current
    /// password. Sends a notification email (no confirm token).</summary>
    Task<ErrorOr<DeletionRequestResponseDto>> RequestDeletionAsync(Guid userId, string password, string? reason, CancellationToken ct = default);

    /// <summary>Cancels a pending deletion. A self-service cancel passes
    /// <paramref name="cancelledByAdminUserId"/>=null; an admin cancel passes
    /// the admin id and works on ANY pending deletion (support escape hatch),
    /// reactivating a user that was deactivated into the admin recycle bin.</summary>
    Task<ErrorOr<bool>> CancelDeletionAsync(Guid userId, Guid? cancelledByAdminUserId = null, CancellationToken ct = default);

    Task<ErrorOr<DeletionStatusDto>> GetDeletionStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Admin-driven permanent erasure (ForceDelete / "empty bin"):
    /// applies Marten data masking, archives the event stream, deletes
    /// secondary documents. Bypasses the AutoPurge toggle.</summary>
    Task<ErrorOr<bool>> PermanentlyEraseAsync(Guid userId, Guid? adminUserId, string reason, CancellationToken ct = default);

    /// <summary>Per-realm self-service sweep (scheduled job): send due reminders +
    /// erase grace-expired self-service deletions. Run inside the realm tenant.</summary>
    Task<(int Reminded, int Erased)> RunSelfServiceSweepAsync(CancellationToken ct = default);

    /// <summary>Per-realm admin recycle-bin auto-purge (scheduled job): erase
    /// admin-pending deletions past retention when AutoPurge is enabled.</summary>
    Task<int> RunAdminRetentionPurgeAsync(CancellationToken ct = default);
}
