using ErrorOr;

namespace TimeToDo.Domain.Errors;

public static class DomainErrors
{
    public static class Todo
    {
        public static Error NotFound(Guid id) => Error.NotFound(
            code: "Todo.NotFound",
            description: $"Todo with ID {id} was not found");

        public static Error HasChildren => Error.Validation(
            code: "Todo.HasChildren",
            description: "Cannot convert a todo with children to a subtodo");

        public static Error IsAlreadySubtodo => Error.Validation(
            code: "Todo.IsAlreadySubtodo",
            description: "Cannot add children to a todo that is already a subtodo");

        public static Error TitleRequired => Error.Validation(
            code: "Todo.TitleRequired",
            description: "Title is required");

        public static Error InvalidStatus => Error.Validation(
            code: "Todo.InvalidStatus",
            description: "Invalid todo status");

        public static Error CannotBeOwnParent => Error.Validation(
            code: "Todo.CannotBeOwnParent",
            description: "A todo cannot be its own parent");

        public static Error CircularReference => Error.Validation(
            code: "Todo.CircularReference",
            description: "Cannot create a circular parent-child reference");

        public static Error ParentNotFound(Guid parentId) => Error.NotFound(
            code: "Todo.ParentNotFound",
            description: $"Parent todo with ID {parentId} was not found");
    }

    public static class Customer
    {
        public static Error NotFound(Guid id) => Error.NotFound(
            code: "Customer.NotFound",
            description: $"Customer with ID {id} was not found");

        public static Error NameRequired => Error.Validation(
            code: "Customer.NameRequired",
            description: "Customer name is required");
    }

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
    }

    public static class Comment
    {
        public static Error NotFound(Guid id) => Error.NotFound(
            code: "Comment.NotFound",
            description: $"Comment with ID {id} was not found");

        public static Error DescriptionRequired => Error.Validation(
            code: "Comment.DescriptionRequired",
            description: "Comment description is required");

        public static Error ReferencedItemRequired => Error.Validation(
            code: "Comment.ReferencedItemRequired",
            description: "Comment must reference an item");
    }
}
