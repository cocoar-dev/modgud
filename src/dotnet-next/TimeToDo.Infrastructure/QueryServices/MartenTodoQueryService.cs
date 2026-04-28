using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.Contracts;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;

namespace TimeToDo.Infrastructure.QueryServices;

public class MartenTodoQueryService(IDocumentSession session) : ITodoQueryService
{
    public async Task<IReadOnlyList<TodoDto>> GetAllAsync(
        Guid? currentUserId = null,
        string? filterId = null,
        string? orderBy = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        var todos = await session.Query<TodoDocument>()
            .Where(t => !t.IsArchived)
            .ToListAsync(ct);

        IEnumerable<TodoDocument> queryable = todos;

        if (!string.IsNullOrEmpty(filterId))
        {
            var guid = ShortGuid.Decode(filterId);
            queryable = queryable.Where(t => t.Id == guid);
        }

        queryable = (orderBy?.ToLower()) switch
        {
            "createdat" => queryable.OrderBy(t => t.CreatedAt),
            "updatedat" => queryable.OrderBy(t => t.UpdatedAt),
            "title" => queryable.OrderBy(t => t.Title),
            _ => queryable.OrderBy(t => t.CreatedAt)
        };

        if (skip.HasValue)
            queryable = queryable.Skip(skip.Value);
        if (take.HasValue)
            queryable = queryable.Take(take.Value);

        var todoList = queryable.ToList();
        return await EnrichTodosAsync(todoList, currentUserId, ct);
    }

    public async Task<IReadOnlyList<TodoDto>> GetArchivedAsync(
        Guid? currentUserId = null,
        CancellationToken ct = default)
    {
        var todos = await session.Query<TodoDocument>()
            .Where(t => t.IsArchived)
            .ToListAsync(ct);

        return await EnrichTodosAsync(todos.ToList(), currentUserId, ct);
    }

    public async Task<TodoDto?> GetByIdAsync(
        Guid id,
        Guid? currentUserId = null,
        CancellationToken ct = default)
    {
        var todo = await session.LoadAsync<TodoDocument>(id, ct);
        if (todo == null) return null;

        var enriched = await EnrichTodosAsync([todo], currentUserId, ct);
        return enriched.FirstOrDefault();
    }

    private async Task<IReadOnlyList<TodoDto>> EnrichTodosAsync(
        List<TodoDocument> todos,
        Guid? currentUserId,
        CancellationToken ct)
    {
        if (!todos.Any())
            return [];

        var todoIds = todos.Select(t => t.Id).ToList();

        // Batch load all comments for all todos
        var allComments = await session.Query<CommentDocument>()
            .Where(c => c.ReferencedItemId.In(todoIds) && c.ReferencedItemType == "todo")
            .ToListAsync(ct);

        var commentsByTodoId = allComments
            .GroupBy(c => c.ReferencedItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Get all read statuses for current user
        var readCommentIds = new HashSet<Guid>();
        if (currentUserId.HasValue && allComments.Any())
        {
            var allCommentIds = allComments.Select(c => c.Id).ToList();
            var readStatuses = await session.Query<CommentReadStatusDocument>()
                .Where(crs => crs.CommentId.In(allCommentIds) && crs.UserId == currentUserId.Value)
                .ToListAsync(ct);
            readCommentIds = readStatuses.Select(rs => rs.CommentId).ToHashSet();
        }

        // Collect all user IDs we need
        var userIds = new HashSet<Guid>();
        foreach (var todo in todos)
        {
            userIds.UnionWith(todo.ResponsibleUserIds);
            if (todo.CreatedById != Guid.Empty) userIds.Add(todo.CreatedById);
            if (todo.UpdatedById.HasValue) userIds.Add(todo.UpdatedById.Value);
        }
        foreach (var comment in allComments)
        {
            if (comment.CreatedById != Guid.Empty) userIds.Add(comment.CreatedById);
        }

        var users = await session.Query<UserDocument>()
            .Where(u => u.Id.In(userIds.ToList()))
            .ToListAsync(ct);
        var userDict = users.ToDictionary(u => u.Id, u => $"{u.Acronym} | {u.Firstname ?? ""} {u.Lastname ?? ""}");

        // Collect all customer IDs
        var customerIds = todos.Where(t => t.CustomerId.HasValue).Select(t => t.CustomerId!.Value).Distinct().ToList();
        var customers = await session.Query<CustomerDocument>()
            .Where(c => c.Id.In(customerIds))
            .ToListAsync(ct);
        var customerDict = customers.ToDictionary(c => c.Id, c => c.Name);

        // Get child todo data for unread comments count
        var allChildIds = todos.SelectMany(t => t.ChildTodoIds).Distinct().ToList();
        var childTodosMap = new Dictionary<Guid, List<TodoDocument>>();
        var childUnreadCounts = new Dictionary<Guid, int>();

        if (allChildIds.Any())
        {
            var childTodos = await session.Query<TodoDocument>()
                .Where(t => t.Id.In(allChildIds))
                .ToListAsync(ct);

            foreach (var todo in todos)
            {
                var children = childTodos.Where(c => todo.ChildTodoIds.Contains(c.Id) && c.IsArchived == todo.IsArchived).ToList();
                childTodosMap[todo.Id] = children;
            }

            if (currentUserId.HasValue)
            {
                var activeChildIds = childTodosMap.Values.SelectMany(l => l.Select(t => t.Id)).Distinct().ToList();
                var childComments = await session.Query<CommentDocument>()
                    .Where(c => c.ReferencedItemId.In(activeChildIds) && c.ReferencedItemType == "todo")
                    .ToListAsync(ct);

                var childCommentIds = childComments.Select(c => c.Id).ToList();
                var childReadStatuses = await session.Query<CommentReadStatusDocument>()
                    .Where(s => s.CommentId.In(childCommentIds) && s.UserId == currentUserId.Value)
                    .Select(s => s.CommentId)
                    .ToListAsync(ct);
                var childReadIds = childReadStatuses.ToHashSet();

                var childCommentsByTodo = childComments
                    .GroupBy(c => c.ReferencedItemId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var todo in todos)
                {
                    var children = childTodosMap.GetValueOrDefault(todo.Id, []);
                    var unreadCount = 0;
                    foreach (var child in children)
                    {
                        var comments = childCommentsByTodo.GetValueOrDefault(child.Id, []);
                        unreadCount += comments.Count(c => !childReadIds.Contains(c.Id));
                    }
                    childUnreadCounts[todo.Id] = unreadCount;
                }
            }
        }

        // Build DTOs
        var result = new List<TodoDto>();
        foreach (var todo in todos)
        {
            var comments = commentsByTodoId.GetValueOrDefault(todo.Id, []);
            var dto = ToDto(todo, userDict, customerDict, comments, readCommentIds, childTodosMap, childUnreadCounts);
            result.Add(dto);
        }

        return result;
    }

    private static TodoDto ToDto(
        TodoDocument document,
        Dictionary<Guid, string> userDict,
        Dictionary<Guid, string> customerDict,
        List<CommentDocument> comments,
        HashSet<Guid> readCommentIds,
        Dictionary<Guid, List<TodoDocument>> childTodosMap,
        Dictionary<Guid, int> childUnreadCounts)
    {
        var dto = new TodoDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Title = document.Title,
            Description = document.Description,
            DueDate = document.DueDate,
            Status = Enum.Parse<TodoStatus>(document.Status, ignoreCase: true),
            Customer = document.CustomerId.HasValue
                ? new RefPropertyDto
                {
                    Id = new ShortGuid(document.CustomerId.Value).ToString(),
                    Label = customerDict.GetValueOrDefault(document.CustomerId.Value)
                }
                : null,
            Responsibles = document.ResponsibleUserIds?
                .Select(id => new RefPropertyDto
                {
                    Id = new ShortGuid(id).ToString(),
                    Label = userDict.GetValueOrDefault(id)
                })
                .ToList() ?? [],
            ParentTodoId = document.ParentTodoId.HasValue
                ? new ShortGuid(document.ParentTodoId.Value).ToString()
                : null,
            Critical = document.IsCritical,
            AwaitingFeedback = document.IsAwaitingFeedback,
            CreatedBy = document.CreatedById != Guid.Empty
                ? new RefPropertyDto
                {
                    Id = new ShortGuid(document.CreatedById).ToString(),
                    Label = userDict.GetValueOrDefault(document.CreatedById)
                }
                : null,
            UpdatedBy = document.UpdatedById.HasValue
                ? new RefPropertyDto
                {
                    Id = new ShortGuid(document.UpdatedById.Value).ToString(),
                    Label = userDict.GetValueOrDefault(document.UpdatedById.Value)
                }
                : null,
            CreatedAt = document.CreatedAt,
            UpdatedAt = document.UpdatedAt,
            LastTouchedAt = document.UpdatedAt ?? document.CreatedAt,
            IsArchived = document.IsArchived,
            AggregateVersion = document.AggregateVersion
        };

        // Child counts
        var children = childTodosMap.GetValueOrDefault(document.Id, []);
        dto.ChildTodosCount = children.Count;
        dto.ChildTodosUnreadCommentsCount = childUnreadCounts.GetValueOrDefault(document.Id, 0);

        // Comments
        dto.Comments = comments.Select(c => new CommentListDto
        {
            Id = new ShortGuid(c.Id).ToString(),
            Description = c.Description,
            CreatedAt = c.CreatedAt,
            CreatedBy = c.CreatedById != Guid.Empty
                ? new RefPropertyDto
                {
                    Id = new ShortGuid(c.CreatedById).ToString(),
                    Label = userDict.GetValueOrDefault(c.CreatedById)
                }
                : null,
            IHaveRead = readCommentIds.Contains(c.Id),
            UpdatedAt = c.UpdatedAt
        }).ToList();

        dto.CommentsCount = comments.Count;
        dto.UnreadComments = comments.Count(c => !readCommentIds.Contains(c.Id));

        return dto;
    }
}
