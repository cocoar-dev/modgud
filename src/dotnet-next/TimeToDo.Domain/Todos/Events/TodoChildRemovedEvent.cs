namespace TimeToDo.Domain.Todos.Events;

public record TodoChildRemovedEvent(Guid ParentId, Guid ChildId);
