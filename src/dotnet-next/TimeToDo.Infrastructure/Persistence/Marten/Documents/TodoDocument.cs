namespace TimeToDo.Infrastructure.Persistence.Marten.Documents;

/// <summary>
/// Todo document - stored as separate document in Marten.
/// References to other entities stored as IDs only (no embedding).
/// </summary>
public class TodoDocument
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime? DueDate { get; set; }
    public required string Status { get; set; }

    // Reference to Customer (by ID only)
    public Guid? CustomerId { get; set; }

    // References to Responsible Users (by IDs only)
    public List<Guid> ResponsibleUserIds { get; set; } = new();

    // Hierarchical structure
    public Guid? ParentTodoId { get; set; }

    // Child todo IDs (denormalized for performance - synced from ParentTodoId)
    // NOTE: ParentTodoId on child is the source of truth; this is derived
    public List<Guid> ChildTodoIds { get; set; } = new();

    public bool IsArchived { get; set; }

    // Flags as explicit properties (easier with documents - no migration needed!)
    public bool IsCritical { get; set; }
    public bool IsAwaitingFeedback { get; set; }

    // Denormalized total comment count (updated when comments are created/deleted)
    // Unread counts are calculated on-demand by loading comments
    public int CommentsCount { get; set; }

    // Audit fields (store IDs only)
    public DateTime CreatedAt { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }

    // Aggregate version for optimistic concurrency
    public int AggregateVersion { get; set; }
}
