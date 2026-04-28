namespace TimeToDo.Domain.Comments.Events;

public record CommentMarkedAsReadEvent(Guid CommentId, Guid UserId, DateTime ReadAt);
