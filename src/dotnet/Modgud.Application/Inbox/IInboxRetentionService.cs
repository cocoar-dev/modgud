namespace Modgud.Application.Inbox;

/// <summary>
/// Periodic sweep that enforces the inbox retention policy. Reads the live
/// <see cref="InboxRetentionSettings"/> document and applies per-kind logic:
/// time-based dismiss for change-request-feedback and scheduled-job-feedback,
/// hard-delete-after-dismiss for admin-change-request items (open items never
/// expire on their own — they get dismissed when the underlying request
/// transitions to a terminal state).
/// </summary>
public interface IInboxRetentionService
{
    Task<InboxRetentionResult> ExecuteAsync(CancellationToken ct = default);
}

/// <summary>
/// Summary of one retention pass. Keys are diagnostic — e.g.
/// <c>ChangeRequestApproved.unread-expired</c>, <c>AdminChangeRequestSubmitted.hard-deleted</c>.
/// </summary>
public sealed record InboxRetentionResult(
    int TotalAffected,
    IReadOnlyDictionary<string, int> AffectedByReason);
