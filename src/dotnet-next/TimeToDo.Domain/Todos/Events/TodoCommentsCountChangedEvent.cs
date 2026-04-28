namespace TimeToDo.Domain.Todos.Events;

public record TodoCommentsCountChangedEvent(Guid Id, int CommentsCount);
