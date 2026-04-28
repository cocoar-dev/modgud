namespace TimeToDo.Domain.Comments.Events;

public record CommentMigratedEvent(
    Guid Id,
    string Description,
    Guid ReferencedItemId,
    string ReferencedItemType,
    DateTime CreatedAt,
    Guid CreatedById,
    DateTime? UpdatedAt,
    Guid? UpdatedById,
    DateTime MigratedAt);
