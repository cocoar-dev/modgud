using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Events;

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
    List<string>? BoundTo = null,
    // Federation v1 (decision G). Trailing optional param so existing positional
    // construction sites are unaffected; old streams replay to default false.
    bool ExternallyDrivable = false);

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
    List<string>? BoundTo = null,
    // Full-replace event: every producer MUST pass the current value or it resets
    // to false. Trailing optional keeps positional callers compiling.
    bool ExternallyDrivable = false);

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
