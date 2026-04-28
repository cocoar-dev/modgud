using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Features.Todos.Queries;

public record GetTodoByIdQuery(Guid TodoId, Guid? UserId);

public class GetTodoByIdHandler(IDocumentSession session, IAccessPolicyEngine accessPolicyEngine)
{
    public async Task<ErrorOr<TodoDto>> Handle(
        GetTodoByIdQuery query,
        CancellationToken ct)
    {
        // Apply the read-scope filter so out-of-scope rows look like "not found" —
        // avoids leaking existence of todos the caller can't see.
        var queryable = session.Query<TodoView>().Where(t => !t.IsDeleted);
        if (query.UserId.HasValue)
        {
            var accessFilter = await accessPolicyEngine.BuildTodoFilterForActionAsync(
                query.UserId.Value, "todo:read", ct);
            if (accessFilter is not null)
                queryable = queryable.Where(accessFilter);
        }

        var todo = await queryable.FirstOrDefaultAsync(t => t.Id == query.TodoId, ct);
        if (todo is null)
            return Error.NotFound("Todo.NotFound", "Todo not found");

        return await todo.ToDtoEnrichedAsync(session, query.UserId, ct: ct);
    }
}
