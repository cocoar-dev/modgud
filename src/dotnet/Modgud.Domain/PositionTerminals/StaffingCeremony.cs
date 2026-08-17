namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// One begun WebAuthn staffing ceremony on an enrolled terminal (MG-FT-05,
/// plan §4.4): the begin endpoint pins WHO may redeem it (the terminal's
/// client + DPoP key), WHAT it activates (position + slot), and the exact
/// assertion options — the token request may not re-choose any of these.
/// Ephemeral like the passkey ceremonies: a plain single-use document
/// (versioned store, consumed BEFORE assertion verification), never
/// event-sourced. TTL five minutes.
/// </summary>
public sealed class StaffingCeremony
{
    public Guid Id { get; set; }

    public Guid PositionPrincipalId { get; set; }
    public Guid TerminalEnrollmentId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string DpopJkt { get; set; } = string.Empty;
    public string MethodId { get; set; } = "personal-passkey";
    public Guid? SubjectUserId { get; set; }
    public Guid? SubjectGrantId { get; set; }
    /// <summary>For a V2 n:m proof-first ceremony, the positions whose
    /// credentials were included in the challenge. This list is server-side
    /// only and is not disclosed until the proof has been verified.</summary>
    public Guid[] CandidatePositionIds { get; set; } = [];
    /// <summary>Present only on the short-lived continuation created after a
    /// valid proof identified more than one position. Each entry carries the
    /// position-specific evidence (notably the correct personal grant id).</summary>
    public StaffingCandidateEvidence[] VerifiedCandidates { get; set; } = [];
    public Guid? StepUpForStaffingSessionId { get; set; }
    public string? StepUpAction { get; set; }
    public string? StepUpNonce { get; set; }
    public string[] StepUpScopes { get; set; } = [];

    public string RpId { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsConsumed => ConsumedAt is not null;
}

public sealed record StaffingCandidateEvidence(
    Guid PositionPrincipalId,
    ActivationEvidence Evidence);
