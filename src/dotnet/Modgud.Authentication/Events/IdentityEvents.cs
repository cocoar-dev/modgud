namespace Modgud.Authentication.Events;

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
    string? IpAddress,
    // Non-PII login-method code (ModgudMeters.LoginMethod.* — "password",
    // "magic_link", "external", …). Trailing optional so old event streams and
    // existing construction sites default to null ("not recorded"). A method
    // switch / first login via a new provider is itself a security signal, so
    // the audit view surfaces it.
    string? Method = null);

public record UserLoginFailedEvent(
    Guid UserId,
    string? IpAddress);

// Aggregated known-user login-failure record (audit redesign Decision (b)): ONE
// event per resolved failure streak — emitted when the access-failed counter
// resets to 0 (successful sign-in / unlock) — NOT one per attempt. Avoids stream
// spam and the amplification vector (an attacker spraying a victim can't inflate
// that victim's stream per attempt). No PII (count + timestamp); lives on the
// user stream, so it erases with the subject.
public record UserLoginFailuresObservedEvent(
    Guid UserId,
    int FailedCount,
    DateTimeOffset ObservedAt);

public record UserLockedOutEvent(
    Guid UserId,
    DateTimeOffset LockoutEnd);

public record UserUnlockedEvent(Guid UserId);
