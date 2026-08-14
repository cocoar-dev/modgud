namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// Stream events for <see cref="PositionGrant"/> (MG-FT-02). The
/// grant document is the inline projection; actor + time of every transition
/// live here, which is the audit trail suspend/revoke decisions lean on.
/// </summary>
public record PositionGrantIssued(
    Guid Id,
    Guid PositionPrincipalId,
    Guid UserId,
    Guid IssuedByUserId,
    DateTimeOffset IssuedAt);

public record PositionGrantSuspended(
    Guid Id,
    Guid SuspendedByUserId,
    DateTimeOffset SuspendedAt);

public record PositionGrantResumed(
    Guid Id,
    Guid ResumedByUserId,
    DateTimeOffset ResumedAt);

public record PositionGrantRevoked(
    Guid Id,
    Guid RevokedByUserId,
    DateTimeOffset RevokedAt);
