namespace Cocoar.Auth.Domain.Events;

// ═══════════════════════════════════════════════════════════════════════════
// ROLE LIFECYCLE EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a new role is created.
/// </summary>
public record RoleCreated(
    Guid RoleId,
    string Name,
    string? Description);

/// <summary>
/// Event raised when a role's name is changed.
/// </summary>
public record RoleNameChanged(
    Guid RoleId,
    string OldName,
    string NewName);

/// <summary>
/// Event raised when a role's description is changed.
/// </summary>
public record RoleDescriptionChanged(
    Guid RoleId,
    string? OldDescription,
    string? NewDescription);

/// <summary>
/// Event raised when a role is deleted.
/// </summary>
public record RoleDeleted(
    Guid RoleId,
    string? Reason);

// ═══════════════════════════════════════════════════════════════════════════
// ROLE CLAIMS EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a claim is added to a role.
/// </summary>
public record RoleClaimAdded(
    Guid RoleId,
    string ClaimType,
    string ClaimValue);

/// <summary>
/// Event raised when a claim is removed from a role.
/// </summary>
public record RoleClaimRemoved(
    Guid RoleId,
    string ClaimType,
    string ClaimValue);

// ═══════════════════════════════════════════════════════════════════════════
// ROLE EXTENDED PROPERTY EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a role's display name is changed.
/// </summary>
public record RoleDisplayNameChanged(
    Guid RoleId,
    string? OldDisplayName,
    string? NewDisplayName);

/// <summary>
/// Event raised when a role's email is changed.
/// </summary>
public record RoleEmailChanged(
    Guid RoleId,
    string? OldEmail,
    string? NewEmail);

/// <summary>
/// Event raised when a role's bound API resource is changed.
/// </summary>
public record RoleBoundToApiResourceChanged(
    Guid RoleId,
    Guid? OldApiResourceId,
    Guid? NewApiResourceId);
