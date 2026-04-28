namespace TimeToDo.AccessPolicy.PoC;

/// <summary>
/// Simplified TodoView for the PoC (no Marten dependency).
/// Mirrors the real TodoView structure.
/// </summary>
public record SimpleTodoView
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime? DueDate { get; init; }
    public string Status { get; init; } = "new";
    public SimpleViewRef? Customer { get; init; }
    public List<SimpleViewRef> Responsibles { get; init; } = [];
    public Guid? ParentTodoId { get; init; }
    public bool IsArchived { get; init; }
    public bool IsCritical { get; init; }
    public bool IsAwaitingFeedback { get; init; }
    public DateTime CreatedAt { get; init; }
    public SimpleViewRef? CreatedBy { get; init; }
    public bool IsDeleted { get; init; }
}

public record SimpleViewRef
{
    public Guid Id { get; init; }
    public string? Label { get; init; }
}
