namespace TimeToDo.Authentication.Events;

// ──────────────────────────────────────────────────────────────
// MIGRATION EVENT — DO NOT DELETE
//
// UserIdentitySetupEvent was introduced during the Auth migration
// (April 2026) to bootstrap existing UserView projections with
// UserName and IsActive fields. It is appended once per user
// by IdentityMigrationService on first startup after deployment.
//
// This event MUST remain in the codebase because:
//   1. It exists in production event streams
//   2. Marten must be able to deserialize it during event replay
//   3. UserViewProjection handles it to populate UserName/IsActive
//
// For normal UserName changes, use UserUserNameChangedEvent instead.
// ──────────────────────────────────────────────────────────────
public record UserIdentitySetupEvent(
    Guid UserId,
    string UserName,
    bool IsActive);

// Regular profile events
public record UserUserNameChangedEvent(
    Guid UserId,
    string UserName);

public record UserProfileUpdatedEvent(
    Guid UserId,
    string? Firstname,
    string? Lastname,
    string? Acronym);

public record UserActivatedEvent(Guid UserId);
public record UserDeactivatedEvent(Guid UserId);

// Security events (metadata only — no hashes!)
public record UserPasswordChangedEvent(
    Guid UserId,
    Guid? ChangedByUserId);

public record UserLoggedInEvent(
    Guid UserId,
    string? IpAddress);

public record UserLoginFailedEvent(
    Guid UserId,
    string? IpAddress);

public record UserLockedOutEvent(
    Guid UserId,
    DateTimeOffset LockoutEnd);

public record UserUnlockedEvent(Guid UserId);
