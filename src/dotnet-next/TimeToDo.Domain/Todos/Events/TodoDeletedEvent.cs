namespace TimeToDo.Domain.Todos.Events;

public record TodoDeletedEvent(Guid Id, DateTime DeletedAt);
