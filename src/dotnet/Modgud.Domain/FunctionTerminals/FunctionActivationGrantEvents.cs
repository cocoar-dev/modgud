namespace Modgud.Domain.FunctionTerminals;

/// <summary>
/// Stream events for <see cref="FunctionActivationGrant"/> (MG-FT-02). The
/// grant document is the inline projection; actor + time of every transition
/// live here, which is the audit trail suspend/revoke decisions lean on.
/// </summary>
public record FunctionActivationGrantIssued(
    Guid Id,
    Guid FunctionPrincipalId,
    Guid UserId,
    Guid IssuedByUserId,
    DateTimeOffset IssuedAt);

public record FunctionActivationGrantSuspended(
    Guid Id,
    Guid SuspendedByUserId,
    DateTimeOffset SuspendedAt);

public record FunctionActivationGrantResumed(
    Guid Id,
    Guid ResumedByUserId,
    DateTimeOffset ResumedAt);

public record FunctionActivationGrantRevoked(
    Guid Id,
    Guid RevokedByUserId,
    DateTimeOffset RevokedAt);
