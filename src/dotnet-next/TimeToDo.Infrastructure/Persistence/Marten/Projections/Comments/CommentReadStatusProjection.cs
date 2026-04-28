using Marten.Events.Aggregation;
using Marten.Events.Projections;
using TimeToDo.Domain.Comments.Events;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;

public class CommentReadStatusProjection : MultiStreamProjection<CommentReadStatus, Guid>
{
    public CommentReadStatusProjection()
    {
        Identity<CommentMarkedAsReadEvent>(e => CommentReadStatus.DeterministicId(e.CommentId, e.UserId));
    }

    public CommentReadStatus Create(CommentMarkedAsReadEvent @event)
    {
        return new CommentReadStatus
        {
            Id = CommentReadStatus.DeterministicId(@event.CommentId, @event.UserId),
            CommentId = @event.CommentId,
            UserId = @event.UserId,
            ReadAt = @event.ReadAt
        };
    }
}
