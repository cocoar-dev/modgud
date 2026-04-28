using ErrorOr;
using Marten;
using BuildingBlocks.Helper;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Application.DTOs;

namespace TimeToDo.Api.Features.Comments.Commands;

public record CreateCommentCommand(
    string? Description,
    Guid ReferencedItemId,
    string ReferencedItemType,
    Guid CreatedById);

public class CreateCommentHandler(IDocumentSession session)
{
    public async Task<ErrorOr<CommentListDto>> Handle(
        CreateCommentCommand command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Description))
            return Error.Validation("Description.Required", "Description is required");

        // Validate referenced item exists
        var validationResult = await ValidateReferencedItemExists(command.ReferencedItemId, command.ReferencedItemType, ct);
        if (validationResult.IsError)
            return validationResult.Errors;

        var commentId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var createdEvent = new CommentCreatedEvent(
            commentId,
            command.Description,
            command.ReferencedItemId,
            command.ReferencedItemType,
            now,
            command.CreatedById);

        var events = new List<object> { createdEvent };

        // Auto-mark as read for creator
        if (command.CreatedById != Guid.Empty)
        {
            events.Add(new CommentMarkedAsReadEvent(commentId, command.CreatedById, now));
        }

        session.Events.StartStream<CommentView>(commentId, events.ToArray());
        await session.SaveChangesAsync(ct);

        // Update Todo's comment count via event
        if (command.ReferencedItemType.Equals("todo", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateTodoCommentCount(command.ReferencedItemId, ct);
        }

        // Build DTO from command data for HTTP response
        var createdBy = await session.LoadAsync<UserView>(command.CreatedById, ct);
        return new CommentListDto
        {
            Id = new ShortGuid(commentId).ToString(),
            Description = command.Description,
            CreatedAt = now,
            CreatedBy = createdBy != null
                ? new RefPropertyDto { Id = new ShortGuid(createdBy.Id).ToString(), Label = createdBy.GetDisplayLabel() }
                : null,
            IHaveRead = true
        };
    }

    private async Task<ErrorOr<Success>> ValidateReferencedItemExists(Guid itemId, string itemType, CancellationToken ct)
    {
        switch (itemType.ToLowerInvariant())
        {
            case "todo":
                var todo = await session.LoadAsync<TodoView>(itemId, ct);
                if (todo == null || todo.IsDeleted)
                    return Error.NotFound("ReferencedItem.NotFound", $"{itemType} with ID {itemId} not found");
                break;
            case "customer":
                var customer = await session.LoadAsync<CustomerView>(itemId, ct);
                if (customer == null)
                    return Error.NotFound("ReferencedItem.NotFound", $"{itemType} with ID {itemId} not found");
                break;
            case "user":
                var user = await session.LoadAsync<UserView>(itemId, ct);
                if (user == null)
                    return Error.NotFound("ReferencedItem.NotFound", $"{itemType} with ID {itemId} not found");
                break;
            default:
                return Error.NotFound("ReferencedItem.NotFound", $"{itemType} with ID {itemId} not found");
        }

        return ErrorOr.Result.Success;
    }

    private async Task UpdateTodoCommentCount(Guid todoId, CancellationToken ct)
    {
        // Use delta-based counting: TodoView.CommentsCount + 1
        // We can't query CommentView here because it's async-projected and may not include the new comment yet
        var todo = await session.LoadAsync<TodoView>(todoId, ct);
        var newCount = (todo?.CommentsCount ?? 0) + 1;

        session.Events.Append(todoId, new TodoCommentsCountChangedEvent(todoId, newCount));
        await session.SaveChangesAsync(ct);
    }
}
