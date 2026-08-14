using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Events;

/// <summary>
/// Stream events for <see cref="FunctionPrincipal"/> (MG-FT-01). Functions are
/// event-sourced like Person and Group — the <see cref="FunctionPrincipal"/>
/// document in the polymorphic principal table is an inline projection
/// (<c>FunctionPrincipalProjection</c>), never written directly.
/// </summary>
public record FunctionPrincipalCreatedEvent(
    Guid Id,
    string AccountName,
    string? Purpose,
    bool IsActive,
    FunctionTerminalPolicy TerminalPolicy);

/// <summary>
/// Full-replace update (mirrors <see cref="GroupUpdatedEvent"/>): every
/// producer passes the complete current state, not a diff.
/// </summary>
public record FunctionPrincipalUpdatedEvent(
    Guid Id,
    string AccountName,
    string? Purpose,
    bool IsActive,
    FunctionTerminalPolicy TerminalPolicy);

public record FunctionPrincipalDeletedEvent(Guid Id);
