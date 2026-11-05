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

    /// <summary>Durable, irreversible erasure intent. Retained across failures so
    /// retries do not need the projection documents or a still-live user stream
    /// to rediscover external-link streams. Null before start and after success.</summary>
    public UserErasureProgress? Erasure { get; set; }

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

    /// <summary>A.gdpr-C002 — an admin delete hit a pending self-service request and
    /// deactivated the account, keeping the user's own request and date. An admin cancel
    /// (restore) reactivates it like an admin-binned account. Only meaningful while
    /// <see cref="IsDeletionPending"/> is <c>true</c>.</summary>
    public bool DeactivatedByAdminDelete { get; set; }

    public string? DeletionReason { get; set; }
    public DateTimeOffset? DataMaskedAt { get; set; }
    public string? DataMaskedReason { get; set; }
    public Guid? DataMaskedByUserId { get; set; }
}

public class UserErasureProgress
{
    public DateTimeOffset StartedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? AdminUserId { get; set; }
    public List<Guid> ExternalLinkIds { get; set; } = [];
    /// <summary>B3-EI-01 — inbox item streams that carry this user's name (admin items
    /// about their change requests, items addressed to them). Recorded with the document
    /// cleanup so a retry masks and archives the same streams.</summary>
    public List<Guid> InboxItemIds { get; set; } = [];
    /// <summary>Committed atomically with the primary document cleanup. Once set,
    /// retries must not append revocation events to a possibly archived stream.</summary>
    public bool DocumentsCleared { get; set; }
}
