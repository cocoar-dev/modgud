using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Domain.Todos.Events;

public record TodoMigratedEvent(
    Guid Id,
    string Title,
    string? Description,
    DateTime? DueDate,
    TodoStatus Status,
    Guid? CustomerId,
    List<Guid> ResponsibleUserIds,
    Guid? ParentTodoId,
    List<Guid> ChildTodoIds,
    bool IsArchived,
    bool IsCritical,
    bool IsAwaitingFeedback,
    int CommentsCount,
    DateTime CreatedAt,
    Guid CreatedById,
    DateTime? UpdatedAt,
    Guid? UpdatedById,
    DateTime MigratedAt);
