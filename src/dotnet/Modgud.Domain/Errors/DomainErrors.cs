using ErrorOr;

namespace Modgud.Domain.Errors;

public static class DomainErrors
{
    public static class User
    {
        public static Error NotFound(Guid id) => Error.NotFound(
            code: "User.NotFound",
            description: $"User with ID {id} was not found");

        public static Error FirstnameRequired => Error.Validation(
            code: "User.FirstnameRequired",
            description: "User firstname is required");

        public static Error LastnameRequired => Error.Validation(
            code: "User.LastnameRequired",
            description: "User lastname is required");

        public static Error UserNameTaken(string userName) => Error.Conflict(
            code: "User.UserNameTaken",
            description: $"Username '{userName}' is already taken");

        public static Error EmailTaken(string email) => Error.Conflict(
            code: "User.EmailTaken",
            description: $"Email '{email}' is already in use");

        public static Error UserNameRequired => Error.Validation(
            code: "User.UserNameRequired",
            description: "Username is required");

        public static Error EmailRequired => Error.Validation(
            code: "User.EmailRequired",
            description: "Email is required");
    }
}
