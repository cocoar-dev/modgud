using ErrorOr;
using Marten;
using BuildingBlocks.Helper;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Features.Todos.Commands;

public record CreateTodoCommand(
    string Title,
    string? Description,
    DateTime? DueDate,
    TodoStatus Status,
    Guid? CustomerId,
    List<Guid> ResponsibleUserIds,
    bool IsCritical,
    bool IsAwaitingFeedback,
    Guid? ParentTodoId,
    Guid CreatedById);

public class CreateTodoHandler(IDocumentSession session)
{
    public async Task<ErrorOr<TodoDto>> Handle(
        CreateTodoCommand command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Title))
            return Error.Validation("Title.Required", "Title is required");

        Guid? customerId = command.CustomerId;

        if (command.ParentTodoId.HasValue)
        {
            var parent = await session.LoadAsync<TodoView>(command.ParentTodoId.Value, ct);
            if (parent is null || parent.IsDeleted)
                return Error.NotFound("Parent.NotFound", "Parent todo not found");
            if (parent.ParentTodoId.HasValue)
                return Error.Validation("Parent.IsSubTodo", "ParentTodo is already a SubTodo!");

            customerId = parent.Customer?.Id;
        }
        else if (command.CustomerId.HasValue)
        {
            var customer = await session.LoadAsync<CustomerView>(command.CustomerId.Value, ct);
            if (customer is null)
                return Error.NotFound("Customer.NotFound", "Customer not found");
        }

        foreach (var userId in command.ResponsibleUserIds)
        {
            var user = await session.LoadAsync<UserView>(userId, ct);
            if (user is null || user.IsDeleted)
                return Error.NotFound("User.NotFound", $"User with ID {userId} not found");
        }

        var todoId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        var createdEvent = new TodoCreatedEvent(
            todoId,
            command.Title,
            command.Description,
            command.DueDate,
            command.Status,
            customerId,
            command.ResponsibleUserIds,
            command.ParentTodoId,
            command.IsCritical,
            command.IsAwaitingFeedback,
            createdAt,
            command.CreatedById);

        session.Events.StartStream<TodoView>(todoId, createdEvent);

        if (command.ParentTodoId.HasValue)
        {
            session.Events.Append(command.ParentTodoId.Value, new TodoChildAddedEvent(command.ParentTodoId.Value, todoId));
        }

        await session.SaveChangesAsync(ct);

        // Build a minimal DTO from command data for the HTTP response (frontend only uses result.Id)
        return new TodoDto
        {
            Id = new ShortGuid(todoId).ToString(),
            Title = command.Title,
            Description = command.Description,
            DueDate = command.DueDate,
            Status = command.Status,
            Critical = command.IsCritical,
            AwaitingFeedback = command.IsAwaitingFeedback,
            CreatedAt = createdAt,
            LastTouchedAt = createdAt,
            Comments = new(),
            CommentsCount = 0,
            UnreadComments = 0,
            ChildTodosCount = 0,
            ChildTodosUnreadCommentsCount = 0,
            IsArchived = false,
            AggregateVersion = 0
        };
    }
}
