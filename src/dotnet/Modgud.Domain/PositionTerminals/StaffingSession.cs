namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// One staffing shift of a position on one terminal (MG-FT-05, plan §4.5):
/// opened by a person's passkey tap, owned by the POSITION (the token subject
/// is the PositionPrincipal — the activating person is security metadata
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

    public Guid PositionPrincipalId { get; set; }
    public Guid TerminalEnrollmentId { get; set; }

    // Security metadata only — never emitted into tokens (plan §7.3).
    public Guid ActivatedByUserId { get; set; }
    public Guid ActivatedByPasskeyCredentialId { get; set; }
    public Guid PositionGrantId { get; set; }

    /// <summary>Versioned, method-neutral activation evidence. Legacy scalar
    /// fields stay projected for query compatibility during the transition.</summary>
    public ActivationEvidence Evidence { get; set; } = new()
    {
        MethodId = "personal-passkey",
        Binding = "dpop",
    };

    public string? DpopJkt { get; set; }
    public string OAuthAuthorizationId { get; set; } = string.Empty;

    public StaffingSessionStatus Status { get; set; }
        = StaffingSessionStatus.Active;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
    public StaffingSessionEndReason? EndReason { get; set; }

    /// <summary>Projection-row compatibility for sessions written before the
    /// Evidence property existed (event rebuilds already use the V1 upcast).</summary>
    public ActivationEvidence GetActivationEvidence()
    {
        if (Evidence.UserId is not null || ActivatedByUserId == Guid.Empty) return Evidence;
        return Evidence with
        {
            MethodId = "personal-passkey",
            UserId = ActivatedByUserId,
            GrantId = PositionGrantId == Guid.Empty ? null : PositionGrantId,
            CredentialId = ActivatedByPasskeyCredentialId == Guid.Empty ? null : ActivatedByPasskeyCredentialId,
            Binding = "dpop",
        };
    }
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
    PositionDisabled,
    TerminalDisabled,
    TerminalRevoked,
    UserDisabled,
    PasskeyDeleted,
    GrantSuspended,
    GrantRevoked,
    OAuthClientDisabled,
    PolicyTightened,
    ActivationCredentialInvalidated,
    ActivationTokenRevoked,
    ActivationTokenUnassigned,
}

/// <summary>
/// Method-neutral evidence captured at activation. Optional identifiers are
/// populated only when the selected proof method owns that concept.
/// </summary>
public sealed record ActivationEvidence
{
    public required string MethodId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? GrantId { get; init; }
    public Guid? CredentialId { get; init; }
    public Guid? ActivationTokenId { get; init; }
    public int? TeamSecretVersion { get; init; }
    public required string Binding { get; init; }
}
