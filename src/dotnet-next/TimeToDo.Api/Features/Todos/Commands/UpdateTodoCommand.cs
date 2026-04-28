using ErrorOr;
using Marten;
using BuildingBlocks.Helper;
using TimeToDo.Domain.Common;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Features.Todos.Commands;

public record UpdateTodoCommand(
    Guid TodoId,
    Optional<string> Title,
    Optional<string?> Description,
    Optional<DateTime?> DueDate,
    Optional<TodoStatus> Status,
    Optional<Guid?> CustomerId,
    Optional<List<Guid>> ResponsibleUserIds,
    Optional<bool> IsCritical,
    Optional<bool> IsAwaitingFeedback,
    Guid UpdatedById);

public class UpdateTodoHandler(IDocumentSession session)
{
    public async Task<ErrorOr<TodoDto>> Handle(
        UpdateTodoCommand command,
        CancellationToken ct)
    {
        var todo = await session.LoadAsync<TodoView>(command.TodoId, ct);
        if (todo is null || todo.IsDeleted)
            return Error.NotFound("Todo.NotFound", "Todo not found");

        if (command.Title.HasValue)
        {
            if (string.IsNullOrWhiteSpace(command.Title.Value))
                return Error.Validation("Title.Required", "Title is required");
        }

        var customerId = command.CustomerId;

        if (command.CustomerId.HasValue)
        {
            if (todo.ParentTodoId.HasValue)
            {
                var parent = await session.LoadAsync<TodoView>(todo.ParentTodoId.Value, ct);
                if (parent != null)
                    customerId = new Optional<Guid?>(parent.Customer?.Id);
            }
            else if (command.CustomerId.Value.HasValue)
            {
                var customer = await session.LoadAsync<CustomerView>(command.CustomerId.Value.Value, ct);
                if (customer is null)
                    return Error.NotFound("Customer.NotFound", "Customer not found");
            }
        }

        if (command.ResponsibleUserIds.HasValue && command.ResponsibleUserIds.Value is { } responsibleIds)
        {
            foreach (var userId in responsibleIds)
            {
                var user = await session.LoadAsync<UserView>(userId, ct);
                if (user is null || user.IsDeleted)
                    return Error.NotFound("User.NotFound", $"User with ID {userId} not found");
            }
        }

        var updatedAt = DateTime.UtcNow;

        var updatedEvent = new TodoUpdatedEvent(
            command.TodoId,
            command.Title,
            command.Description,
            command.DueDate,
            command.Status,
            customerId,
            command.ResponsibleUserIds,
            command.IsCritical,
            command.IsAwaitingFeedback,
            updatedAt,
            command.UpdatedById);

        session.Events.Append(command.TodoId, updatedEvent);
        await session.SaveChangesAsync(ct);

        // Build DTO from pre-update view + command changes for HTTP response
        var dto = todo.ToDto();
        if (command.Title.HasValue && command.Title.Value is { } updatedTitle) dto.Title = updatedTitle;
        if (command.Description.HasValue) dto.Description = command.Description.Value;
        if (command.DueDate.HasValue) dto.DueDate = command.DueDate.Value;
        if (command.Status.HasValue) dto.Status = command.Status.Value;
        if (command.IsCritical.HasValue) dto.Critical = command.IsCritical.Value;
        if (command.IsAwaitingFeedback.HasValue) dto.AwaitingFeedback = command.IsAwaitingFeedback.Value;
        dto.UpdatedAt = updatedAt;
        dto.LastTouchedAt = updatedAt;

        return dto;
    }
}
