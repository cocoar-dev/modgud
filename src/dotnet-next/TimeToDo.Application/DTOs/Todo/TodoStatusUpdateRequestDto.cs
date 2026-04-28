using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Application.DTOs.Todo;

public class TodoStatusUpdateRequestDto
{
    public TodoStatus Status { get; set; }

    public List<string> Ids { get; set; } = new();
}
