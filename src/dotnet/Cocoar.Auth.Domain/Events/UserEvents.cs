namespace Cocoar.Auth.Domain.Events;

// ═══════════════════════════════════════════════════════════════════════════
// PROFILE EVENTS (with data - auditable changes)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a new user is created.
/// </summary>
public record UserCreated(
    Guid UserId,
    string UserName,
    string? Email,
    string? PhoneNumber,
    string? FirstName,
    string? LastName,
    bool IsActive,
    bool LockoutEnabled,
    List<Guid> Roles);

/// <summary>
/// Event raised when a user's username is changed.
/// </summary>
public record UserNameChanged(
    Guid UserId,
    string OldUserName,
    string NewUserName);

/// <summary>
/// Event raised when a user's email is changed.
/// </summary>
public record UserEmailChanged(
    Guid UserId,
    string? OldEmail,
    string? NewEmail);

/// <summary>
/// Event raised when a user's phone number is changed.
/// </summary>
public record UserPhoneNumberChanged(
    Guid UserId,
    string? OldPhoneNumber,
    string? NewPhoneNumber);

/// <summary>
/// Event raised when a user's name (first/last) is changed.
/// </summary>
public record UserProfileNameChanged(
    Guid UserId,
    string? OldFirstName,
    string? OldLastName,
    string? NewFirstName,
    string? NewLastName);

/// <summary>
/// Event raised when a user's expiration date is changed.
/// </summary>
public record UserExpirationChanged(
    Guid UserId,
    DateTimeOffset? OldExpiresAt,
    DateTimeOffset? NewExpiresAt);

/// <summary>
/// Event raised when a user is activated.
/// </summary>
public record UserActivated(Guid UserId);

/// <summary>
/// Event raised when a user is deactivated.
/// </summary>
public record UserDeactivated(
    Guid UserId,
    string? Reason);

/// <summary>
/// Event raised when a user is deleted (soft delete).
/// </summary>
public record UserDeleted(
    Guid UserId,
    string? Reason);

// ═══════════════════════════════════════════════════════════════════════════
// ROLE EVENTS (with data)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a role is assigned to a user.
/// </summary>
public record UserRoleAssigned(
    Guid UserId,
    Guid RoleId);

/// <summary>
/// Event raised when a role is removed from a user.
/// </summary>
public record UserRoleRemoved(
    Guid UserId,
    Guid RoleId);

// ═══════════════════════════════════════════════════════════════════════════
// CLAIM EVENTS (with data - auditable changes)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a claim is added to a user.
/// </summary>
public record UserClaimAdded(
    Guid UserId,
    string ClaimType,
    string ClaimValue);

/// <summary>
/// Event raised when a claim is removed from a user.
/// </summary>
public record UserClaimRemoved(
    Guid UserId,
    string ClaimType,
    string ClaimValue);

// ═══════════════════════════════════════════════════════════════════════════
// SECURITY EVENTS (metadata only - no sensitive data stored)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a user's password is changed.
/// Does NOT contain the password hash - only metadata about the change.
/// </summary>
public record UserPasswordChanged(
    Guid UserId,
    PasswordChangeType ChangeType,
    Guid? ChangedByUserId);

/// <summary>
/// The type of password change that occurred.
/// </summary>
public enum PasswordChangeType
{
    /// <summary>User changed their own password.</summary>
    UserChange,
    /// <summary>Admin reset the password.</summary>
    AdminReset,
    /// <summary>Password reset via forgot password flow.</summary>
    ForgotPassword
}

/// <summary>
/// Event raised when two-factor authentication is enabled for a user.
/// </summary>
public record UserTwoFactorEnabled(Guid UserId);

/// <summary>
/// Event raised when two-factor authentication is disabled for a user.
/// </summary>
public record UserTwoFactorDisabled(Guid UserId);

/// <summary>
/// Event raised when recovery codes are regenerated.
/// Does NOT contain the actual codes.
/// </summary>
public record UserRecoveryCodesRegenerated(
    Guid UserId,
    int CodeCount);

/// <summary>
/// Event raised when all user sessions are invalidated (force logout).
/// </summary>
public record UserSessionsInvalidated(
    Guid UserId,
    string Reason,
    Guid? InvalidatedByUserId);

// ═══════════════════════════════════════════════════════════════════════════
// AUTH EVENTS (for security monitoring/audit)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a user successfully logs in.
/// </summary>
public record UserLoggedIn(
    Guid UserId,
    string? IpAddress,
    string? UserAgent);

/// <summary>
/// Event raised when a login attempt fails.
/// </summary>
public record UserLoginFailed(
    Guid UserId,
    string? IpAddress,
    string? UserAgent,
    LoginFailureReason FailureReason);

/// <summary>
/// The reason a login attempt failed.
/// </summary>
public enum LoginFailureReason
{
    /// <summary>Invalid password provided.</summary>
    InvalidPassword,
    /// <summary>Account is locked out.</summary>
    LockedOut,
    /// <summary>Two-factor authentication failed.</summary>
    TwoFactorFailed,
    /// <summary>Account is not active.</summary>
    AccountInactive,
    /// <summary>Account is not allowed to sign in.</summary>
    NotAllowed
}

/// <summary>
/// Event raised when a user account is locked out.
/// </summary>
public record UserLockedOut(
    Guid UserId,
    DateTimeOffset? LockoutEnd,
    LockoutReason Reason);

/// <summary>
/// The reason a user was locked out.
/// </summary>
public enum LockoutReason
{
    /// <summary>Too many failed login attempts.</summary>
    TooManyFailedAttempts,
    /// <summary>Administrator manually locked the account.</summary>
    AdminAction
}

/// <summary>
/// Event raised when a user account is unlocked.
/// </summary>
public record UserUnlocked(
    Guid UserId,
    Guid? UnlockedByUserId);

// ═══════════════════════════════════════════════════════════════════════════
// VERIFICATION EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a user's email is confirmed.
/// </summary>
public record UserEmailConfirmed(Guid UserId);

/// <summary>
/// Event raised when a user's phone number is confirmed.
/// </summary>
public record UserPhoneNumberConfirmed(Guid UserId);

// ═══════════════════════════════════════════════════════════════════════════
// GDPR / DATA PROTECTION EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a user requests account deletion.
/// This initiates the deletion workflow with a confirmation period.
/// </summary>
public record UserDeletionRequested(
    Guid UserId,
    string? Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset ConfirmationDeadline);

/// <summary>
/// Event raised when a user cancels their deletion request.
/// </summary>
public record UserDeletionCancelled(
    Guid UserId,
    DateTimeOffset CancelledAt);

/// <summary>
/// Event raised when a user's data has been masked (GDPR erasure).
/// PII is replaced with masked values but audit trail is preserved.
/// </summary>
public record UserDataMasked(
    Guid UserId,
    DateTimeOffset MaskedAt,
    Guid? MaskedByUserId,
    string MaskingReason);

/// <summary>
/// Event raised when a user's data has been exported (GDPR portability).
/// </summary>
public record UserDataExported(
    Guid UserId,
    DateTimeOffset ExportedAt,
    string ExportFormat);

/// <summary>
/// Event raised when a soft-deleted user is restored.
/// </summary>
public record UserRestored(
    Guid UserId,
    Guid? RestoredByUserId,
    string? Reason);

// ═══════════════════════════════════════════════════════════════════════════
// EMAIL OTP EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when an email OTP is requested for two-factor authentication.
/// </summary>
public record UserEmailOtpRequested(
    Guid UserId,
    string? IpAddress);

/// <summary>
/// Event raised when an email OTP is successfully verified.
/// </summary>
public record UserEmailOtpVerified(Guid UserId);

// ═══════════════════════════════════════════════════════════════════════════
// WEBAUTHN EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a WebAuthn credential is registered for a user.
/// </summary>
public record WebAuthnCredentialRegistered(
    Guid UserId,
    string CredentialId,
    string? DeviceName);

/// <summary>
/// Event raised when a WebAuthn credential is deleted.
/// </summary>
public record WebAuthnCredentialDeleted(
    Guid UserId,
    string CredentialId);

/// <summary>
/// Event raised when a WebAuthn credential is used for authentication.
/// </summary>
public record WebAuthnCredentialUsed(
    Guid UserId,
    string CredentialId,
    string? IpAddress);
