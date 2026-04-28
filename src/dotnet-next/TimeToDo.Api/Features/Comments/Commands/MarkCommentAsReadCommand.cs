using ErrorOr;
using Marten;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Api.Features.Comments.Commands;

public record MarkCommentAsReadCommand(Guid CommentId, Guid UserId);

public class MarkCommentAsReadHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        MarkCommentAsReadCommand command,
        CancellationToken ct)
    {
        var comment = await session.LoadAsync<CommentView>(command.CommentId, ct);
        if (comment == null || comment.IsDeleted)
            return Error.NotFound("Comment.NotFound", "Comment not found");

        var user = await session.LoadAsync<UserView>(command.UserId, ct);
        if (user == null || user.IsDeleted)
            return Error.NotFound("User.NotFound", "User not found");

        // Idempotent — early return if already read
        var alreadyRead = await session.Query<CommentReadStatus>()
            .AnyAsync(rs => rs.CommentId == command.CommentId && rs.UserId == command.UserId, ct);
        if (alreadyRead)
            return ErrorOr.Result.Success;

        session.Events.Append(command.CommentId, new CommentMarkedAsReadEvent(command.CommentId, command.UserId, DateTime.UtcNow));

        // Trigger TodoView SignalR update so all clients get the updated UnreadComments count.
        // We re-emit the current CommentsCount — the projection is a no-op but RaiseSideEffects
        // fires and the TodoHub enriches per-user UnreadComments from the now-updated read status.
        if (comment.ReferencedItemType == "todo")
        {
            var todo = await session.LoadAsync<TodoView>(comment.ReferencedItemId, ct);
            session.Events.Append(comment.ReferencedItemId,
                new TodoCommentsCountChangedEvent(comment.ReferencedItemId, todo?.CommentsCount ?? 0));
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
