using TimeToDo.Domain.Common;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Application.DTOs.Todo;

public class TodoUpdateDto
{
    public Optional<string> Title { get; set; }
    public Optional<string?> Description { get; set; }
    public Optional<DateTime?> DueDate { get; set; }
    public Optional<TodoStatus> Status { get; set; }
    public Optional<RefPropertyDto?> Customer { get; set; }
    public Optional<List<RefPropertyDto>> Responsibles { get; set; }
    public Optional<bool> Critical { get; set; }
    public Optional<bool> AwaitingFeedback { get; set; }
}
