namespace Cocoar.Auth.Authentication.Domain;

/// <summary>
/// A user's self-service change request. One open request per (User, Type) aggregates
/// every pending edit; the next edit on an open request merges into its <see cref="Payload"/>
/// instead of opening a parallel one. Terminal requests (Approved/Rejected) are kept for
/// audit; the next edit after a terminal state opens a fresh request.
///
/// The payload is intentionally opaque to the domain — it's the JSON-serialized DTO
/// specific to <see cref="Type"/>. The endpoint that handles a given type knows how to
/// merge submissions into it; the admin-approve handler knows how to deserialize and
/// apply it to the target aggregate. Adding a new request type adds a new endpoint and
/// a new approve branch — no domain changes.
///
/// Email changes require ownership proof via an emailed token (currently the only field
/// that does). While an unverified email sits in the payload the whole request is
/// <see cref="ChangeRequestStatus.EmailVerificationPending"/>; other edits ride along
/// and move to admin approval together once the email is verified.
/// </summary>
public class UserChangeRequest
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ChangeRequestType Type { get; set; } = ChangeRequestType.Profile;

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Opaque JSON payload whose shape is decided by <see cref="Type"/>.</summary>
    public string Payload { get; set; } = "{}";

    public ChangeRequestStatus Status { get; set; }

    public string? VerificationTokenHash { get; set; }
    public DateTimeOffset? VerificationExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewerNote { get; set; }
}

public enum ChangeRequestType
{
    Profile = 0,
}

public enum ChangeRequestStatus
{
    EmailVerificationPending = 0,
    AdminApprovalPending = 1,
    Approved = 2,
    Rejected = 3,
}
