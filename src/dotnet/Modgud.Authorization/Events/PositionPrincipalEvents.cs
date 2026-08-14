using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Events;

/// <summary>
/// Stream events for <see cref="PositionPrincipal"/> (MG-FT-01). Positions are
/// event-sourced like Person and Group — the <see cref="PositionPrincipal"/>
/// document in the polymorphic principal table is an inline projection
/// (<c>PositionPrincipalProjection</c>), never written directly.
/// </summary>
public record PositionPrincipalCreatedEvent(
    Guid Id,
    string AccountName,
    string? Purpose,
    bool IsActive,
    PositionTerminalPolicy TerminalPolicy);

/// <summary>
/// Full-replace update (mirrors <see cref="GroupUpdatedEvent"/>): every
/// producer passes the complete current state, not a diff.
/// </summary>
public record PositionPrincipalUpdatedEvent(
    Guid Id,
    string AccountName,
    string? Purpose,
    bool IsActive,
    PositionTerminalPolicy TerminalPolicy);

public record PositionPrincipalDeletedEvent(Guid Id);
