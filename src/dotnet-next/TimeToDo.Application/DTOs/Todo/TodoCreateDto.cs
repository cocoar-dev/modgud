using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Application.DTOs.Todo;

public class TodoCreateDto
{
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime? DueDate { get; set; }
    public TodoStatus Status { get; set; } = TodoStatus.New;

    public RefPropertyDto? Customer { get; set; }

    public List<RefPropertyDto>? Responsibles { get; set; }

    public bool Critical { get; set; }
    public bool AwaitingFeedback { get; set; }
}
