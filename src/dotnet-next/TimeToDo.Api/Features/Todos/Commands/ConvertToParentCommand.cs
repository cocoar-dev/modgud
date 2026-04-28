using ErrorOr;
using Marten;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos.Commands;

public record ConvertToParentCommand(List<Guid> TodoIds);

public class ConvertToParentHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        ConvertToParentCommand command,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var todoId in command.TodoIds)
        {
            var todo = await session.LoadAsync<TodoView>(todoId, ct);
            if (todo is null || todo.IsDeleted)
                continue;

            if (todo.ParentTodoId.HasValue)
            {
                var parentId = todo.ParentTodoId.Value;

                // Remove child from parent
                session.Events.Append(parentId, new TodoChildRemovedEvent(parentId, todoId));

                // Clear parent on child
                session.Events.Append(todoId, new TodoParentChangedEvent(todoId, null, null, now));
            }
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
