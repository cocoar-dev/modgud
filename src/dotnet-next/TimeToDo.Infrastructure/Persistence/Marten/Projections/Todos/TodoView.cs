using Marten.Schema;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

[DocumentAlias("todo_view")]
public record TodoView
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime? DueDate { get; init; }
    public TodoStatus Status { get; init; }
    public ViewRef? Customer { get; init; }
    public List<ViewRef> Responsibles { get; init; } = new();
    public Guid? ParentTodoId { get; init; }
    public List<Guid> ChildTodoIds { get; init; } = new();
    public bool IsArchived { get; init; }
    public bool IsCritical { get; init; }
    public bool IsAwaitingFeedback { get; init; }
    public int CommentsCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public ViewRef? CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public ViewRef? UpdatedBy { get; init; }
    public bool IsDeleted { get; init; }
}
