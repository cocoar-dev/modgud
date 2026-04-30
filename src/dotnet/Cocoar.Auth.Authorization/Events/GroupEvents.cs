using Cocoar.Auth.Authorization.Principals;

namespace Cocoar.Auth.Authorization.Events;

public record GroupCreatedEvent(
    Guid Id,
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? CompiledMembershipScript = null,
    List<string>? MembershipScriptDependencies = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared,
    List<string>? BoundTo = null);

public record GroupUpdatedEvent(
    Guid Id,
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? CompiledMembershipScript = null,
    List<string>? MembershipScriptDependencies = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared,
    List<string>? BoundTo = null);

public record GroupMembershipRecomputedEvent(
    Guid Id,
    List<Guid> MemberIds);

/// <summary>
/// Emitted when an automatic membership recompute fails (transpile, translate,
/// or query exception). <c>MemberIds</c> stay unchanged — only
/// <see cref="Principals.Group.MembershipLastError"/> is updated so the UI can
/// distinguish "script matched nobody" from "script errored".
/// </summary>
public record GroupMembershipRecomputeFailedEvent(
    Guid Id,
    string Error);

public record GroupDeletedEvent(Guid Id);
