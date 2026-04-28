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
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Features.Todos.Queries;

public record GetAllTodosQuery(
    Guid? UserId,
    string? FilterId = null,
    string? OrderBy = null,
    int? Skip = null,
    int? Take = null);

public class GetAllTodosHandler(IDocumentSession session, IAccessPolicyEngine accessPolicyEngine)
{
    public async Task<ErrorOr<List<TodoDto>>> Handle(
        GetAllTodosQuery query,
        CancellationToken ct)
    {
        // Query 1: Load all todos
        var queryable = session.Query<TodoView>()
            .Where(t => !t.IsArchived && !t.IsDeleted);

        // Apply access policy filter
        if (query.UserId.HasValue)
        {
            var accessFilter = await accessPolicyEngine.BuildTodoFilterForActionAsync(query.UserId.Value, "todo:read", ct);
            if (accessFilter is not null)
                queryable = queryable.Where(accessFilter);
        }

        if (!string.IsNullOrEmpty(query.FilterId))
        {
            var guid = ShortGuid.Decode(query.FilterId);
            queryable = queryable.Where(t => t.Id == guid);
        }

        var ordered = (query.OrderBy?.ToLower()) switch
        {
            "createdat" => queryable.OrderBy(t => t.CreatedAt),
            "updatedat" => queryable.OrderBy(t => t.UpdatedAt),
            "title" => queryable.OrderBy(t => t.Title),
            _ => queryable.OrderBy(t => t.CreatedAt)
        };

        IQueryable<TodoView> paged = ordered;
        if (query.Skip.HasValue)
            paged = paged.Skip(query.Skip.Value);
        if (query.Take.HasValue)
            paged = paged.Take(query.Take.Value);

        var todoList = await paged.ToListAsync(ct);

        if (!todoList.Any())
            return new List<TodoDto>();

        var todoIds = todoList.Select(t => t.Id).ToList();
        var todoIdSet = new HashSet<Guid>(todoIds);

        // Query 2: Batch-load all comments for these todos
        var allComments = await session.Query<CommentView>()
            .Where(c => c.ReferencedItemId.In(todoIds) && c.ReferencedItemType == "todo" && !c.IsDeleted)
            .ToListAsync(ct);

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

            // Child todos: since we load ALL non-archived todos, children are in the result set
            if (todo.ChildTodoIds.Any())
            {
                var childCount = todo.ChildTodoIds.Count(id => todoIdSet.Contains(id));
                dto.ChildTodosCount = childCount;

                if (query.UserId.HasValue)
                {
                    var childComments = todo.ChildTodoIds
                        .Where(id => todoIdSet.Contains(id))
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
