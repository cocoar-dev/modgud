using ErrorOr;
using Marten;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos.Commands;

public record ArchiveTodosCommand(List<Guid> TodoIds, bool Restore);

public class ArchiveTodosHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        ArchiveTodosCommand command,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var id in command.TodoIds)
        {
            var todo = await session.LoadAsync<TodoView>(id, ct);
            if (todo is null || todo.IsDeleted)
                continue;

            var isArchived = !command.Restore;

            session.Events.Append(id, new TodoArchivedEvent(
                id,
                isArchived,
                now,
                todo.UpdatedBy?.Id ?? todo.CreatedBy?.Id));
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
