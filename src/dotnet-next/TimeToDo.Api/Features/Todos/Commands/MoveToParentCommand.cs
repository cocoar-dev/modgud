using ErrorOr;
using Marten;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos.Commands;

public record MoveToParentCommand(Guid SubTodoId, Guid ParentTodoId);

public class MoveToParentHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        MoveToParentCommand command,
        CancellationToken ct)
    {
        var subtodo = await session.LoadAsync<TodoView>(command.SubTodoId, ct);
        if (subtodo is null || subtodo.IsDeleted)
            return Error.NotFound("SubTodo.NotFound", "SubTodo not found");

        // Check if subtodo has children (can't move a parent with children)
        if (subtodo.ChildTodoIds.Any())
            return Error.Validation("SubTodo.HasChildren", "Todo has SubTodos!");

        var parentTodo = await session.LoadAsync<TodoView>(command.ParentTodoId, ct);
        if (parentTodo is null || parentTodo.IsDeleted)
            return Error.NotFound("Parent.NotFound", "Parent todo not found");

        if (parentTodo.ParentTodoId.HasValue)
            return Error.Validation("Parent.IsSubTodo", "ParentTodo is already a SubTodo!");

        var now = DateTime.UtcNow;

        // Remove from old parent if different
        if (subtodo.ParentTodoId.HasValue && subtodo.ParentTodoId.Value != command.ParentTodoId)
        {
            session.Events.Append(subtodo.ParentTodoId.Value, new TodoChildRemovedEvent(subtodo.ParentTodoId.Value, command.SubTodoId));
        }

        // Update child's parent reference and inherit customer
        session.Events.Append(command.SubTodoId, new TodoParentChangedEvent(
            command.SubTodoId,
            command.ParentTodoId,
            parentTodo.Customer?.Id,
            now));

        // Add child to new parent
        session.Events.Append(command.ParentTodoId, new TodoChildAddedEvent(command.ParentTodoId, command.SubTodoId));

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
