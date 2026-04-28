namespace TimeToDo.Domain.Comments.Events;

public record CommentCreatedEvent(
    Guid Id,
    string Description,
    Guid ReferencedItemId,
    string ReferencedItemType,
    DateTime CreatedAt,
    Guid CreatedById);
