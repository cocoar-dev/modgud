using ErrorOr;
using Marten;
using TimeToDo.Domain.Common;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Features.Todos.Commands;

public record UpdateTodoFlagsCommand(
    List<Guid> TodoIds,
    List<string>? AddFlags,
    List<string>? RemoveFlags,
    Guid? UpdatedById);

public class UpdateTodoFlagsHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateTodoFlagsCommand command,
        CancellationToken ct)
    {
        var updatedAt = DateTime.UtcNow;

        foreach (var id in command.TodoIds)
        {
            var todo = await session.LoadAsync<TodoView>(id, ct);
            if (todo is null || todo.IsDeleted)
                continue;

            var isCritical = Optional<bool>.None;
            var isAwaitingFeedback = Optional<bool>.None;

            if (command.AddFlags is not null)
            {
                foreach (var flag in command.AddFlags)
                {
                    switch (flag.ToLowerInvariant())
                    {
                        case "critical":
                            isCritical = true;
                            break;
                        case "awaitingfeedback":
                            isAwaitingFeedback = true;
                            break;
                    }
                }
            }

            if (command.RemoveFlags is not null)
            {
                foreach (var flag in command.RemoveFlags)
                {
                    switch (flag.ToLowerInvariant())
                    {
                        case "critical":
                            isCritical = false;
                            break;
                        case "awaitingfeedback":
                            isAwaitingFeedback = false;
                            break;
                    }
                }
            }

            session.Events.Append(id, new TodoFlagsChangedEvent(
                id,
                isCritical,
                isAwaitingFeedback,
                updatedAt,
                command.UpdatedById));
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
