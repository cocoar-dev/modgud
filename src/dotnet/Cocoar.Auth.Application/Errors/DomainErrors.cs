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

public static class EmailOtpErrors
{
    public static Error InvalidCode => Error.Validation(
        code: "EmailOtp.InvalidCode",
        description: "The verification code is invalid.");

    public static Error Expired => Error.Validation(
        code: "EmailOtp.Expired",
        description: "The verification code has expired. Please request a new one.");

    public static Error TooManyAttempts => Error.Validation(
        code: "EmailOtp.TooManyAttempts",
        description: "Too many failed attempts. Please request a new code.");

    public static Error AlreadySent => Error.Validation(
        code: "EmailOtp.AlreadySent",
        description: "A verification code was recently sent. Please wait before requesting a new one.");

    public static Error NoPendingChallenge => Error.Validation(
        code: "EmailOtp.NoPendingChallenge",
        description: "No pending verification code found. Please request a new one.");

    public static Error EmailRequired => Error.Validation(
        code: "EmailOtp.EmailRequired",
        description: "A verified email address is required to use email OTP.");
}

public static class WebAuthnErrors
{
    public static Error InvalidChallenge => Error.Validation(
        code: "WebAuthn.InvalidChallenge",
        description: "The authentication challenge is invalid or has expired.");

    public static Error AttestationFailed => Error.Validation(
        code: "WebAuthn.AttestationFailed",
        description: "Failed to verify the credential registration.");

    public static Error AssertionFailed => Error.Validation(
        code: "WebAuthn.AssertionFailed",
        description: "Failed to verify the authentication response.");

    public static Error CredentialNotFound => Error.NotFound(
        code: "WebAuthn.CredentialNotFound",
        description: "The specified credential was not found.");

    public static Error SignCountMismatch => Error.Validation(
        code: "WebAuthn.SignCountMismatch",
        description: "Credential sign count indicates possible cloned authenticator.");

    public static Error NoCredentialsRegistered => Error.Validation(
        code: "WebAuthn.NoCredentialsRegistered",
        description: "No WebAuthn credentials are registered for this account.");

    public static Error CredentialAlreadyRegistered => Error.Conflict(
        code: "WebAuthn.CredentialAlreadyRegistered",
        description: "This credential is already registered.");

    public static Error UserNotFound => Error.NotFound(
        code: "WebAuthn.UserNotFound",
        description: "User not found for the specified credential.");
}

public static class LoginProviderErrors
{
    public static Error NotFound(string id) => Error.NotFound(
        code: "LoginProvider.NotFound",
        description: $"Login provider with ID '{id}' was not found.");

    public static Error DuplicateName(string name) => Error.Conflict(
        code: "LoginProvider.DuplicateName",
        description: $"A login provider with name '{name}' already exists.");

    public static Error CannotDeleteBuiltIn(string name) => Error.Validation(
        code: "LoginProvider.CannotDeleteBuiltIn",
        description: $"Cannot delete the built-in login provider '{name}'.");
}

public static class ExternalLoginErrors
{
    public static Error ProviderNotFound(string name) => Error.NotFound(
        code: "ExternalLogin.ProviderNotFound",
        description: $"External login provider '{name}' was not found.");

    public static Error ProviderNotOidc(string name) => Error.Validation(
        code: "ExternalLogin.ProviderNotOidc",
        description: $"Login provider '{name}' is not an OpenID Connect provider.");

    public static Error InvalidState => Error.Validation(
        code: "ExternalLogin.InvalidState",
        description: "Invalid or expired external login state.");

    public static Error TokenExchangeFailed => Error.Failure(
        code: "ExternalLogin.TokenExchangeFailed",
        description: "Failed to exchange authorization code for tokens.");

    public static Error IdTokenValidationFailed => Error.Validation(
        code: "ExternalLogin.IdTokenValidationFailed",
        description: "Failed to validate the ID token from the external provider.");

    public static Error ExternalLoginAlreadyLinked => Error.Conflict(
        code: "ExternalLogin.AlreadyLinked",
        description: "This external login is already linked to another account.");

    public static Error CannotUnlinkOnlyLogin => Error.Validation(
        code: "ExternalLogin.CannotUnlinkOnlyLogin",
        description: "Cannot unlink the only login method. Set a password or link another provider first.");

    public static Error MissingConfiguration(string key) => Error.Validation(
        code: "ExternalLogin.MissingConfiguration",
        description: $"The login provider is missing required configuration: '{key}'.");

    public static Error UserAccountInactive => Error.Validation(
        code: "ExternalLogin.UserAccountInactive",
        description: "The user account is not active.");
}

public static class OAuthErrors
{
    public static Error ClientIdAlreadyExists(string clientId) => Error.Conflict(
        code: "OAuth.ClientIdAlreadyExists",
        description: $"An OAuth client with ID '{clientId}' already exists.");

    public static Error ClientNotFound(string id) => Error.NotFound(
        code: "OAuth.ClientNotFound",
        description: $"OAuth client with ID '{id}' was not found.");

    public static Error InvalidClientType(string clientType) => Error.Validation(
        code: "OAuth.InvalidClientType",
        description: $"Invalid client type '{clientType}'. Must be 'public' or 'confidential'.");

    public static Error InvalidConsentType(string consentType) => Error.Validation(
        code: "OAuth.InvalidConsentType",
        description: $"Invalid consent type '{consentType}'. Must be 'explicit', 'implicit', or 'external'.");

    public static Error CannotRegenerateSecretForPublicClient => Error.Validation(
        code: "OAuth.CannotRegenerateSecretForPublicClient",
        description: "Cannot regenerate secret for a public client. Only confidential clients have secrets.");

    public static Error ScopeNameAlreadyExists(string name) => Error.Conflict(
        code: "OAuth.ScopeNameAlreadyExists",
        description: $"An OAuth scope with name '{name}' already exists.");

    public static Error ScopeNotFound(string id) => Error.NotFound(
        code: "OAuth.ScopeNotFound",
        description: $"OAuth scope with ID '{id}' was not found.");

    public static Error CannotModifyStandardScope(string name) => Error.Validation(
        code: "OAuth.CannotModifyStandardScope",
        description: $"Cannot modify the standard scope '{name}'.");

    public static Error CannotDeleteStandardScope(string name) => Error.Validation(
        code: "OAuth.CannotDeleteStandardScope",
        description: $"Cannot delete the standard scope '{name}'.");

    public static Error ApiNameAlreadyExists(string name) => Error.Conflict(
        code: "OAuth.ApiNameAlreadyExists",
        description: $"An API with name '{name}' already exists.");

    public static Error ApiNotFound(string id) => Error.NotFound(
        code: "OAuth.ApiNotFound",
        description: $"API with ID '{id}' was not found.");

    public static Error ApiSecretNotFound(string secretId) => Error.NotFound(
        code: "OAuth.ApiSecretNotFound",
        description: $"API secret with ID '{secretId}' was not found.");
}
