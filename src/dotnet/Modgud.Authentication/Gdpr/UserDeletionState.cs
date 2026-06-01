namespace Modgud.Authentication.Gdpr;

/// <summary>
/// Who initiated a pending deletion. Drives the lifecycle rules: a
/// <see cref="SelfService"/> request keeps the user active so they can log
/// in and cancel during the grace window; an <see cref="Admin"/> request
/// deactivates the user (recycle bin) and only an admin can restore it.
/// </summary>
public enum DeletionInitiator
{
    SelfService = 0,
    Admin = 1,
}

/// <summary>
/// Per-user deletion bookkeeping used by the account-lifecycle flows:
/// tracks a pending deletion (self-service grace window or admin recycle
/// bin) plus the terminal "data masked" flag once permanent erasure ran.
/// Stored as a regular Marten document keyed on the user id.
///
/// <para>Email reservation keys on the <c>ApplicationUser.IsDeleted</c>
/// flag, NOT on this document: restorable states (active, self-service
/// pending, admin recycle-bin) all keep <c>IsDeleted=false</c>, so the
/// partial unique index <c>WHERE is_deleted = false</c> keeps the email
/// reserved through the whole restorable window. Release happens only at
/// the irreversible permanent erase, atomically with nulling the email.</para>
/// </summary>
public class UserDeletionState
{
    /// <summary>Same id as the <c>ApplicationUser</c>.</summary>
    public Guid Id { get; set; }

    public bool IsDeletionPending { get; set; }
    public bool IsDataMasked { get; set; }

    /// <summary>Who asked for the deletion. Only meaningful while
    /// <see cref="IsDeletionPending"/> is <c>true</c>. Decides whether the
    /// user can self-cancel (self-service) or is in the admin recycle bin.</summary>
    public DeletionInitiator? DeletionInitiator { get; set; }

    /// <summary>The admin user id that initiated an
    /// <see cref="DeletionInitiator.Admin"/> deletion; <c>null</c> for a
    /// self-service request (the user is the requester).</summary>
    public Guid? DeletionRequestedByUserId { get; set; }

    public DateTimeOffset? DeletionRequestedAt { get; set; }

    /// <summary>The moment the pending deletion turns into a permanent erase
    /// (self-service grace expiry) or becomes eligible for auto-purge (admin
    /// recycle-bin retention expiry). Replaces the old confirm-token deadline.</summary>
    public DateTimeOffset? DeletionConfirmationDeadline { get; set; }

    /// <summary>When the "deletion is approaching" reminder email was last
    /// sent for the current pending request; <c>null</c> = not yet sent.
    /// Keeps the scheduled reminder job idempotent (send once per window).</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    public string? DeletionReason { get; set; }
    public DateTimeOffset? DataMaskedAt { get; set; }
    public string? DataMaskedReason { get; set; }
    public Guid? DataMaskedByUserId { get; set; }
}
