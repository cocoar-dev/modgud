namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// Events of a <see cref="StaffingSession"/> stream (MG-FT-05). Exactly two:
/// a session starts once and ends once (ending is idempotent at the endpoint
/// layer — no second Ended is ever appended). All activation metadata is
/// fixed at start; there is nothing to mutate in between.
/// </summary>
public record StaffingSessionStarted(
    Guid Id,
    Guid PositionPrincipalId,
    Guid TerminalEnrollmentId,
    Guid ActivatedByUserId,
    Guid ActivatedByPasskeyCredentialId,
    Guid PositionGrantId,
    string DpopJkt,
    string OAuthAuthorizationId,
    DateTimeOffset StartedAt,
    DateTimeOffset AbsoluteExpiresAt);

public record StaffingSessionEnded(
    Guid Id,
    StaffingSessionEndReason Reason,
    DateTimeOffset EndedAt);
