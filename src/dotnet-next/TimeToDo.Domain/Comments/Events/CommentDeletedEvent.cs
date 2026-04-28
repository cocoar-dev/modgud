namespace TimeToDo.Domain.Comments.Events;

public record CommentDeletedEvent(Guid Id, DateTime DeletedAt);
