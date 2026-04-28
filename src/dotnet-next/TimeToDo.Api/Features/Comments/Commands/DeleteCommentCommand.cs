using ErrorOr;
using Marten;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Comments.Commands;

public record DeleteCommentCommand(Guid CommentId, Guid? CurrentUserId = null);

public class DeleteCommentHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteCommentCommand command,
        CancellationToken ct)
    {
        var comment = await session.LoadAsync<CommentView>(command.CommentId, ct);
        if (comment == null || comment.IsDeleted)
            return Error.NotFound("Comment.NotFound", "Comment not found");

        session.Events.Append(command.CommentId, new CommentDeletedEvent(command.CommentId, DateTime.UtcNow));
        await session.SaveChangesAsync(ct);

        // Update Todo's comment count via event
        // Use delta-based counting: TodoView.CommentsCount - 1
        // We can't query CommentView here because it's async-projected and may still include the deleted comment
        if (comment.ReferencedItemType.Equals("todo", StringComparison.OrdinalIgnoreCase))
        {
            var todo = await session.LoadAsync<TodoView>(comment.ReferencedItemId, ct);
            var newCount = Math.Max(0, (todo?.CommentsCount ?? 0) - 1);

            session.Events.Append(comment.ReferencedItemId, new TodoCommentsCountChangedEvent(comment.ReferencedItemId, newCount));
            await session.SaveChangesAsync(ct);
        }

        return ErrorOr.Result.Success;
    }
}
