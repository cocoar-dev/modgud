namespace Cocoar.Auth.Domain.Events;

// ═══════════════════════════════════════════════════════════════════════════
// GROUP LIFECYCLE EVENTS
// ═══════════════════════════════════════════════════════════════════════════

public record GroupCreated(
	Guid GroupId,
	string Name,
	string? Description);

public record GroupRenamed(
	Guid GroupId,
	string OldName,
	string NewName);

public record GroupDescriptionChanged(
	Guid GroupId,
	string? OldDescription,
	string? NewDescription);

public record GroupArchived(
	Guid GroupId,
	string? Reason);

// ═══════════════════════════════════════════════════════════════════════════
// GROUP MEMBERSHIP EVENTS
// ═══════════════════════════════════════════════════════════════════════════

public record GroupMemberAdded(
	Guid GroupId,
	Guid UserId);

public record GroupMemberRemoved(
	Guid GroupId,
	Guid UserId);

// ═══════════════════════════════════════════════════════════════════════════
// GROUP NESTING EVENTS
// ═══════════════════════════════════════════════════════════════════════════

public record GroupChildAdded(
	Guid GroupId,
	Guid ChildGroupId);

public record GroupChildRemoved(
	Guid GroupId,
	Guid ChildGroupId);

// ═══════════════════════════════════════════════════════════════════════════
// GROUP ROLE GRANT EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// A realm role was granted to this group.
/// </summary>
public record GroupRealmRoleGranted(
	Guid GroupId,
	Guid RoleId);

public record GroupRealmRoleRevoked(
	Guid GroupId,
	Guid RoleId);

/// <summary>
/// A client role was granted to this group, scoped to a specific OAuth client.
/// </summary>
public record GroupClientRoleGranted(
	Guid GroupId,
	Guid RoleId,
	Guid ClientId);

public record GroupClientRoleRevoked(
	Guid GroupId,
	Guid RoleId,
	Guid ClientId);
