using ErrorOr;
using Marten;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos.Commands;

public record DeleteTodosCommand(List<Guid> TodoIds);

public class DeleteTodosHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteTodosCommand command,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var id in command.TodoIds)
        {
            var todo = await session.LoadAsync<TodoView>(id, ct);
            if (todo is null || todo.IsDeleted)
                continue;

            // Orphan children that are NOT in the delete list
            if (todo.ChildTodoIds.Any())
            {
                foreach (var childId in todo.ChildTodoIds)
                {
                    if (command.TodoIds.Contains(childId))
                        continue;

                    var child = await session.LoadAsync<TodoView>(childId, ct);
                    if (child != null && !child.IsDeleted && child.ParentTodoId == id)
                    {
                        session.Events.Append(childId, new TodoParentChangedEvent(childId, null, null, now));
                        session.Events.Append(id, new TodoChildRemovedEvent(id, childId));
                    }
                }
            }

            // Remove from parent
            if (todo.ParentTodoId.HasValue)
            {
                var parent = await session.LoadAsync<TodoView>(todo.ParentTodoId.Value, ct);
                if (parent != null && !parent.IsDeleted && parent.ChildTodoIds.Contains(id))
                {
                    session.Events.Append(todo.ParentTodoId.Value, new TodoChildRemovedEvent(todo.ParentTodoId.Value, id));
                }
            }

            session.Events.Append(id, new TodoDeletedEvent(id, now));
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
