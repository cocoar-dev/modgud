using ErrorOr;
using Marten;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Features.Todos.Commands;

public record UpdateTodoStatusCommand(
    List<Guid> TodoIds,
    TodoStatus Status,
    Guid? UpdatedById);

public class UpdateTodoStatusHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateTodoStatusCommand command,
        CancellationToken ct)
    {
        var updatedAt = DateTime.UtcNow;

        foreach (var id in command.TodoIds)
        {
            var todo = await session.LoadAsync<TodoView>(id, ct);
            if (todo is null || todo.IsDeleted)
                continue;

            session.Events.Append(id, new TodoStatusChangedEvent(
                id,
                command.Status,
                updatedAt,
                command.UpdatedById));
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
