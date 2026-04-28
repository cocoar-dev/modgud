namespace TimeToDo.Infrastructure.Persistence.Marten.Documents;

/// <summary>
/// Comment document - stored as separate document in Marten.
/// Can be attached to different entity types (polymorphic).
/// Read tracking is handled via CommentReadStatusDocument join table.
/// </summary>
public class CommentDocument
{
    public Guid Id { get; set; }
    public required string Description { get; set; }

    // Reference to parent entity (polymorphic)
    public Guid ReferencedItemId { get; set; }
    public required string ReferencedItemType { get; set; } // e.g., "todo"

    // Audit fields (store IDs only)
    public DateTime CreatedAt { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}
