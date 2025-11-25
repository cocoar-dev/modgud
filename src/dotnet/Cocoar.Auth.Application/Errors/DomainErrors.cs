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
