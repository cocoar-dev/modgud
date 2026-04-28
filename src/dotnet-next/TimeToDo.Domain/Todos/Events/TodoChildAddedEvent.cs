namespace TimeToDo.Domain.Todos.Events;

public record TodoChildAddedEvent(Guid ParentId, Guid ChildId);
