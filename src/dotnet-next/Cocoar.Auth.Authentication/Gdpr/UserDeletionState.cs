namespace Cocoar.Auth.Authentication.Gdpr;

/// <summary>
/// Per-user deletion bookkeeping used by the GDPR self-service flow:
/// tracks a pending deletion request (with confirmation deadline) plus
/// the terminal "data masked" flag once permanent erasure ran. Stored
/// as a regular Marten document keyed on the user id.
/// </summary>
public class UserDeletionState
{
    /// <summary>Same id as the <c>ApplicationUser</c>.</summary>
    public Guid Id { get; set; }

    public bool IsDeletionPending { get; set; }
    public bool IsDataMasked { get; set; }

    public DateTimeOffset? DeletionRequestedAt { get; set; }
    public DateTimeOffset? DeletionConfirmationDeadline { get; set; }
    public string? DeletionReason { get; set; }
    public DateTimeOffset? DataMaskedAt { get; set; }
    public string? DataMaskedReason { get; set; }
    public Guid? DataMaskedByUserId { get; set; }
}
