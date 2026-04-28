using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Infrastructure.Persistence.Marten.Projections;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

public static class TodoViewMapper
{
    public static TodoDto ToDto(this TodoView view)
    {
        return new TodoDto
        {
            Id = new ShortGuid(view.Id).ToString(),
            Title = view.Title,
            Description = view.Description,
            DueDate = view.DueDate,
            Status = view.Status,
            Customer = view.Customer?.ToRefPropertyDto(),
            Responsibles = view.Responsibles
                .Select(r => r.ToRefPropertyDto())
                .ToList(),
            ParentTodoId = view.ParentTodoId.HasValue
                ? new ShortGuid(view.ParentTodoId.Value).ToString()
                : null,
            Critical = view.IsCritical,
            AwaitingFeedback = view.IsAwaitingFeedback,
            CreatedBy = view.CreatedBy?.ToRefPropertyDto(),
            UpdatedBy = view.UpdatedBy?.ToRefPropertyDto(),
            CreatedAt = view.CreatedAt,
            UpdatedAt = view.UpdatedAt,
            LastTouchedAt = view.UpdatedAt ?? view.CreatedAt,
            Comments = new(),
            UnreadComments = 0,
            CommentsCount = view.CommentsCount,
            ChildTodosCount = view.ChildTodoIds.Count,
            ChildTodosUnreadCommentsCount = 0,
            IsArchived = view.IsArchived,
            AggregateVersion = 0
        };
    }

    public static async Task<TodoDto> ToDtoEnrichedAsync(
        this TodoView view,
        IDocumentSession session,
        Guid? currentUserId = null,
        List<CommentView>? comments = null,
        CancellationToken ct = default)
    {
        var dto = view.ToDto();

        // Calculate ChildTodosCount and ChildTodosUnreadCommentsCount
        if (view.ChildTodoIds.Any())
        {
            var childTodos = await session.Query<TodoView>()
                .Where(t => t.Id.In(view.ChildTodoIds) && t.IsArchived == view.IsArchived && !t.IsDeleted)
                .ToListAsync(ct);

            dto.ChildTodosCount = childTodos.Count;

            if (currentUserId.HasValue)
            {
                var childTodoIds = childTodos.Select(t => t.Id).ToList();
                var childComments = await session.Query<CommentView>()
                    .Where(c => c.ReferencedItemId.In(childTodoIds) && c.ReferencedItemType == "todo" && !c.IsDeleted)
                    .ToListAsync(ct);

                var childCommentIds = childComments.Select(c => c.Id).ToList();
                var readChildCommentIds = new HashSet<Guid>(
                    await session.Query<CommentReadStatus>()
                        .Where(rs => rs.UserId == currentUserId.Value && rs.CommentId.In(childCommentIds))
                        .Select(rs => rs.CommentId)
                        .ToListAsync(ct));

                dto.ChildTodosUnreadCommentsCount = childComments.Count(c => !readChildCommentIds.Contains(c.Id));
            }
        }

        // Fetch and enrich Comments if user is provided
        if (currentUserId.HasValue)
        {
            var todoComments = comments ?? await session.Query<CommentView>()
                .Where(c => c.ReferencedItemId == view.Id && c.ReferencedItemType == "todo" && !c.IsDeleted)
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);

            var todoCommentIds = todoComments.Select(c => c.Id).ToList();
            var readCommentIds = new HashSet<Guid>(
                await session.Query<CommentReadStatus>()
                    .Where(rs => rs.UserId == currentUserId.Value && rs.CommentId.In(todoCommentIds))
                    .Select(rs => rs.CommentId)
                    .ToListAsync(ct));

            dto.Comments = todoComments.Select(c => new CommentListDto
            {
                Id = new ShortGuid(c.Id).ToString(),
                Description = c.Description,
                CreatedAt = c.CreatedAt,
                CreatedBy = c.CreatedBy?.ToRefPropertyDto(),
                IHaveRead = readCommentIds.Contains(c.Id),
                UpdatedAt = c.UpdatedAt
            }).ToList();

            dto.CommentsCount = todoComments.Count;
            dto.UnreadComments = todoComments.Count(c => !readCommentIds.Contains(c.Id));
        }

        return dto;
    }

    private static RefPropertyDto ToRefPropertyDto(this ViewRef viewRef)
    {
        return new RefPropertyDto
        {
            Id = new ShortGuid(viewRef.Id).ToString(),
            Label = viewRef.Label,
            PrincipalType = viewRef.PrincipalType,
        };
    }
}
