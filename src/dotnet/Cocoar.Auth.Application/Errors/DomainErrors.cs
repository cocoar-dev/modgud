using ErrorOr;

namespace Cocoar.Auth.Application.Errors;

public static class AuthErrors
{
    public static Error InvalidCredentials => Error.Validation(
        code: "Auth.InvalidCredentials",
        description: "Invalid username or password.");

    public static Error UserLockedOut => Error.Validation(
        code: "Auth.UserLockedOut",
        description: "This account has been locked out. Please try again later.");

    public static Error UserNotAllowed => Error.Validation(
        code: "Auth.UserNotAllowed",
        description: "This account is not allowed to sign in.");

    public static Error RequiresTwoFactor => Error.Validation(
        code: "Auth.RequiresTwoFactor",
        description: "Two-factor authentication is required.");

    public static Error UserNotFound => Error.NotFound(
        code: "Auth.UserNotFound",
        description: "User not found.");

    public static Error NotAuthenticated => Error.Unauthorized(
        code: "Auth.NotAuthenticated",
        description: "User is not authenticated.");

    public static Error EmailNotConfirmed => Error.Validation(
        code: "Auth.EmailNotConfirmed",
        description: "Email address has not been confirmed.");

    public static Error EmailAlreadyConfirmed => Error.Validation(
        code: "Auth.EmailAlreadyConfirmed",
        description: "Email address has already been confirmed.");

    public static Error InvalidEmailConfirmationToken => Error.Validation(
        code: "Auth.InvalidEmailConfirmationToken",
        description: "Invalid or expired email confirmation token.");

    public static Error InvalidPasswordResetToken => Error.Validation(
        code: "Auth.InvalidPasswordResetToken",
        description: "Invalid or expired password reset token.");

    public static Error RegistrationFailed(IEnumerable<string> errors) => Error.Validation(
        code: "Auth.RegistrationFailed",
        description: string.Join("; ", errors));

    public static Error PasswordResetFailed(IEnumerable<string> errors) => Error.Validation(
        code: "Auth.PasswordResetFailed",
        description: string.Join("; ", errors));
}

public static class UserErrors
{
    public static Error NotFound(Guid id) => Error.NotFound(
        code: "User.NotFound",
        description: $"User with ID '{id}' was not found.");

    public static Error NotFoundByUserName(string userName) => Error.NotFound(
        code: "User.NotFound",
        description: $"User with username '{userName}' was not found.");

    public static Error DuplicateUserName(string userName) => Error.Conflict(
        code: "User.DuplicateUserName",
        description: $"A user with username '{userName}' already exists.");

    public static Error DuplicateEmail(string email) => Error.Conflict(
        code: "User.DuplicateEmail",
        description: $"A user with email '{email}' already exists.");

    public static Error CreationFailed(IEnumerable<string> errors) => Error.Validation(
        code: "User.CreationFailed",
        description: string.Join("; ", errors));

    public static Error UpdateFailed(IEnumerable<string> errors) => Error.Validation(
        code: "User.UpdateFailed",
        description: string.Join("; ", errors));

    public static Error PasswordChangeFailed(IEnumerable<string> errors) => Error.Validation(
        code: "User.PasswordChangeFailed",
        description: string.Join("; ", errors));

    public static Error InvalidPassword => Error.Validation(
        code: "User.InvalidPassword",
        description: "The current password is incorrect.");

    public static Error NotLockedOut(Guid id) => Error.Validation(
        code: "User.NotLockedOut",
        description: $"User with ID '{id}' is not currently locked out.");
}

public static class TwoFactorErrors
{
    public static Error InvalidVerificationCode => Error.Validation(
        code: "TwoFactor.InvalidVerificationCode",
        description: "The verification code is invalid.");

    public static Error InvalidRecoveryCode => Error.Validation(
        code: "TwoFactor.InvalidRecoveryCode",
        description: "The recovery code is invalid or has already been used.");

    public static Error TwoFactorNotEnabled => Error.Validation(
        code: "TwoFactor.NotEnabled",
        description: "Two-factor authentication is not enabled for this account.");

    public static Error FailedToGenerateKey => Error.Failure(
        code: "TwoFactor.FailedToGenerateKey",
        description: "Failed to generate authenticator key.");

    public static Error FailedToGenerateCodes => Error.Failure(
        code: "TwoFactor.FailedToGenerateCodes",
        description: "Failed to generate recovery codes.");

    public static Error NoTwoFactorUser => Error.Validation(
        code: "TwoFactor.NoTwoFactorUser",
        description: "No two-factor authentication user found. Please login first.");
}

public static class SessionErrors
{
    public static Error NotFound(Guid id) => Error.NotFound(
        code: "Session.NotFound",
        description: $"Session with ID '{id}' was not found.");

    public static Error NotOwner => Error.Forbidden(
        code: "Session.NotOwner",
        description: "You do not have permission to manage this session.");
}

public static class GdprErrors
{
    public static Error DeletionAlreadyRequested => Error.Conflict(
        code: "Gdpr.DeletionAlreadyRequested",
        description: "A deletion request is already pending for this account.");

    public static Error NoDeletionPending => Error.Validation(
        code: "Gdpr.NoDeletionPending",
        description: "No deletion request is pending for this account.");

    public static Error DeletionExpired => Error.Validation(
        code: "Gdpr.DeletionExpired",
        description: "The deletion confirmation period has expired. Please request deletion again.");

    public static Error InvalidDeletionToken => Error.Validation(
        code: "Gdpr.InvalidDeletionToken",
        description: "Invalid or expired deletion confirmation token.");

    public static Error UserAlreadyDeleted => Error.Conflict(
        code: "Gdpr.UserAlreadyDeleted",
        description: "This user account has already been deleted.");

    public static Error UserNotDeleted => Error.Validation(
        code: "Gdpr.UserNotDeleted",
        description: "This user account is not deleted.");

    public static Error DataAlreadyMasked => Error.Conflict(
        code: "Gdpr.DataAlreadyMasked",
        description: "User data has already been masked. This operation cannot be undone.");

    public static Error CannotRestoreMaskedUser => Error.Validation(
        code: "Gdpr.CannotRestoreMaskedUser",
        description: "Cannot restore a user whose data has been permanently erased.");
}

public static class RoleErrors
{
    public static Error NotFound(Guid id) => Error.NotFound(
        code: "Role.NotFound",
        description: $"Role with ID '{id}' was not found.");

    public static Error NotFoundByName(string name) => Error.NotFound(
        code: "Role.NotFound",
        description: $"Role with name '{name}' was not found.");

    public static Error DuplicateName(string name) => Error.Conflict(
        code: "Role.DuplicateName",
        description: $"A role with name '{name}' already exists.");

    public static Error CreationFailed(IEnumerable<string> errors) => Error.Validation(
        code: "Role.CreationFailed",
        description: string.Join("; ", errors));

    public static Error UpdateFailed(IEnumerable<string> errors) => Error.Validation(
        code: "Role.UpdateFailed",
        description: string.Join("; ", errors));

    public static Error CannotDeleteWithUsers => Error.Validation(
        code: "Role.CannotDeleteWithUsers",
        description: "Cannot delete a role that has users assigned to it.");
}
