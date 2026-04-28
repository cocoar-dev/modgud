namespace TimeToDo.Domain.Todos.Events;

public record TodoArchivedEvent(
    Guid Id,
    bool IsArchived,
    DateTime UpdatedAt,
    Guid? UpdatedById);
