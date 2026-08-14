namespace Modgud.Domain.FunctionTerminals;

/// <summary>
/// One physical terminal slot of a function (MG-FT-03, plan §4.3). Owns exactly
/// one terminal-managed public OAuth client (1:1, enforced by the unique
/// indexes on <see cref="ClientId"/>/<see cref="OAuthApplicationId"/> and the
/// function-terminal client invariant). <see cref="DpopJkt"/> is empty until
/// the device-flow enrollment succeeds (MG-FT-04) and immutable afterwards —
/// key rotation is never silent, it is a fresh enrollment on a fresh slot.
/// <see cref="ActiveStaffingSessionId"/> is the synchronization boundary for
/// "at most one active session per terminal" (written by MG-FT-05).
///
/// <para>Event-sourced: this document is the inline projection of the
/// enrollment stream, never written directly.</para>
/// </summary>
public sealed class TerminalEnrollment
{
    public Guid Id { get; set; }
    public Guid FunctionPrincipalId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Location { get; set; }

    public Guid OAuthApplicationId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string WebAuthnRpId { get; set; } = string.Empty;

    public string? DpopJkt { get; set; }
    public string? EnrollmentAuthorizationId { get; set; }

    public TerminalEnrollmentStatus Status { get; set; }
        = TerminalEnrollmentStatus.Pending;

    public Guid? ActiveStaffingSessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum TerminalEnrollmentStatus
{
    Pending,
    Active,
    Disabled,
    Revoked,
}
