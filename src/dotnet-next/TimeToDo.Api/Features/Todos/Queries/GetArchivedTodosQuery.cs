using ErrorOr;
using Marten;
using BuildingBlocks.Helper;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos.Queries;

public record GetArchivedTodosQuery(Guid? UserId);

public class GetArchivedTodosHandler(IDocumentSession session, IAccessPolicyEngine accessPolicyEngine)
{
    public async Task<ErrorOr<List<TodoDto>>> Handle(
        GetArchivedTodosQuery query,
        CancellationToken ct)
    {
        // Query 1: Load all archived todos within the caller's read scope
        var queryable = session.Query<TodoView>().Where(t => t.IsArchived && !t.IsDeleted);
        if (query.UserId.HasValue)
        {
            var accessFilter = await accessPolicyEngine.BuildTodoFilterForActionAsync(
                query.UserId.Value, "todo:read", ct);
            if (accessFilter is not null)
                queryable = queryable.Where(accessFilter);
        }

        var todoList = await queryable.ToListAsync(ct);

        if (!todoList.Any())
            return new List<TodoDto>();

        var todoIds = todoList.Select(t => t.Id).ToList();
        var todoIdSet = new HashSet<Guid>(todoIds);

        // Collect child todo IDs that aren't already in our result set
        var allChildTodoIds = todoList
            .SelectMany(t => t.ChildTodoIds)
            .Distinct()
            .Where(id => !todoIdSet.Contains(id))
            .ToList();

        // Query 2a: Load child todos not in the archived set
        var childTodosTask = allChildTodoIds.Any()
            ? session.Query<TodoView>().Where(t => t.Id.In(allChildTodoIds) && !t.IsDeleted).ToListAsync(ct)
            : Task.FromResult<IReadOnlyList<TodoView>>(new List<TodoView>());

        // Query 2b: Batch-load all comments for these todos (plus child todos)
        var allRelevantTodoIds = todoIds.Concat(allChildTodoIds).Distinct().ToList();
        var allCommentsTask = session.Query<CommentView>()
            .Where(c => c.ReferencedItemId.In(allRelevantTodoIds) && c.ReferencedItemType == "todo" && !c.IsDeleted)
            .ToListAsync(ct);

        await Task.WhenAll(childTodosTask, allCommentsTask);

        var allChildTodos = await childTodosTask;
        var allComments = await allCommentsTask;

        var childTodosById = allChildTodos.ToDictionary(t => t.Id);
        var commentsByTodoId = allComments
            .GroupBy(c => c.ReferencedItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Query 3: Batch-load read statuses for current user
        HashSet<Guid> readCommentIds = new();
        if (query.UserId.HasValue && allComments.Any())
        {
            var allCommentIds = allComments.Select(c => c.Id).ToList();
            var readStatuses = await session.Query<CommentReadStatus>()
                .Where(rs => rs.UserId == query.UserId.Value && rs.CommentId.In(allCommentIds))
                .Select(rs => rs.CommentId)
                .ToListAsync(ct);
            readCommentIds = new HashSet<Guid>(readStatuses);
        }

        // In-memory: map to DTOs with enrichment
        var enrichedTodos = new List<TodoDto>(todoList.Count);
        foreach (var todo in todoList)
        {
            var dto = todo.ToDto();
            var comments = commentsByTodoId.GetValueOrDefault(todo.Id, []);

            if (todo.ChildTodoIds.Any())
            {
                // Count children that match the same archived state
                var matchingChildIds = todo.ChildTodoIds.Where(id =>
                {
                    if (todoIdSet.Contains(id)) return true; // in our archived set
                    return childTodosById.TryGetValue(id, out var child) && child.IsArchived == todo.IsArchived;
                }).ToList();

                dto.ChildTodosCount = matchingChildIds.Count;

                if (query.UserId.HasValue)
                {
                    var childComments = matchingChildIds
                        .SelectMany(id => commentsByTodoId.GetValueOrDefault(id, []))
                        .ToList();
                    dto.ChildTodosUnreadCommentsCount = childComments.Count(c => !readCommentIds.Contains(c.Id));
                }
            }

            dto.Comments = comments.Select(c => new CommentListDto
            {
                Id = new ShortGuid(c.Id).ToString(),
                Description = c.Description,
                CreatedAt = c.CreatedAt,
                CreatedBy = c.CreatedBy != null
                    ? new RefPropertyDto { Id = new ShortGuid(c.CreatedBy.Id).ToString(), Label = c.CreatedBy.Label }
                    : null,
                IHaveRead = query.UserId.HasValue && readCommentIds.Contains(c.Id),
                UpdatedAt = c.UpdatedAt
            }).ToList();

            dto.CommentsCount = comments.Count;
            dto.UnreadComments = query.UserId.HasValue ? comments.Count(c => !readCommentIds.Contains(c.Id)) : 0;

            enrichedTodos.Add(dto);
        }

        return enrichedTodos;
    }
}
