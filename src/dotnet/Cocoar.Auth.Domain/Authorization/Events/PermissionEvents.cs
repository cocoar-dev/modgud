using Cocoar.Auth.Domain.Principals;

namespace Cocoar.Auth.Domain.Authorization.Events;

// ── Permission Role events ──────────────────────────────────

public record PermissionRoleCreatedEvent(
    Guid Id,
    string Name,
    string? Description,
    string ResourceType,
    List<string> Permissions);

public record PermissionRoleUpdatedEvent(
    Guid Id,
    string Name,
    string? Description,
    string ResourceType,
    List<string> Permissions);

public record PermissionRoleDeletedEvent(Guid Id);

// ── Authorization Group events ──────────────────────────────

public record AuthorizationGroupCreatedEvent(
    Guid Id,
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    List<ResourceAccessScript> AccessScripts,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? CompiledMembershipScript = null,
    List<string>? MembershipScriptDependencies = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared);

public record AuthorizationGroupUpdatedEvent(
    Guid Id,
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    List<ResourceAccessScript> AccessScripts,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? CompiledMembershipScript = null,
    List<string>? MembershipScriptDependencies = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared);

public record AuthorizationGroupMembershipRecomputedEvent(
    Guid Id,
    List<Guid> MemberIds);

/// <summary>
/// Emitted when an automatic membership recompute fails (transpile, translate,
/// or query exception). MemberIds remain unchanged — only <c>MembershipLastError</c>
/// is updated so the UI can distinguish "script matched nobody" from "script errored".
/// </summary>
public record AuthorizationGroupMembershipRecomputeFailedEvent(
    Guid Id,
    string Error);

public record AuthorizationGroupDeletedEvent(Guid Id);
