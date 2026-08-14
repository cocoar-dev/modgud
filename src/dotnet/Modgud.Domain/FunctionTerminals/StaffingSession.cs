namespace Modgud.Domain.FunctionTerminals;

/// <summary>
/// One staffing shift of a function on one terminal (MG-FT-05, plan §4.5):
/// opened by a person's passkey tap, owned by the FUNCTION (the token subject
/// is the FunctionPrincipal — the activating person is security metadata
/// only, never the business actor). At most one active session per terminal
/// (the terminal stream's <c>ActiveStaffingSessionId</c> is the lock).
/// <see cref="AbsoluteExpiresAt"/> is fixed at start and never moved by a
/// refresh; ending the session revokes exactly
/// <see cref="OAuthAuthorizationId"/>.
///
/// <para>Event-sourced: this document is the inline projection of the
/// session stream, never written directly.</para>
/// </summary>
public sealed class StaffingSession
{
    public Guid Id { get; set; }

    public Guid FunctionPrincipalId { get; set; }
    public Guid TerminalEnrollmentId { get; set; }

    // Security metadata only — never emitted into tokens (plan §7.3).
    public Guid ActivatedByUserId { get; set; }
    public Guid ActivatedByPasskeyCredentialId { get; set; }
    public Guid FunctionActivationGrantId { get; set; }

    public string DpopJkt { get; set; } = string.Empty;
    public string OAuthAuthorizationId { get; set; } = string.Empty;

    public StaffingSessionStatus Status { get; set; }
        = StaffingSessionStatus.Active;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
    public StaffingSessionEndReason? EndReason { get; set; }
}

public enum StaffingSessionStatus
{
    Active,
    Ended,
}

public enum StaffingSessionEndReason
{
    LocalLock,
    RemoteLock,
    ReplacedByNewActivation,
    Expired,
    FunctionDisabled,
    TerminalDisabled,
    TerminalRevoked,
    UserDisabled,
    PasskeyDeleted,
    GrantSuspended,
    GrantRevoked,
    OAuthClientDisabled,
}
