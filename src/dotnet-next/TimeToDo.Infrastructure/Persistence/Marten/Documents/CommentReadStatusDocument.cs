namespace TimeToDo.Infrastructure.Persistence.Marten.Documents;

/// <summary>
/// Tracks which users have read which comments.
/// Normalized join table for efficient querying.
/// </summary>
public class CommentReadStatusDocument
{
    public Guid Id { get; set; }
    public Guid CommentId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ReadAt { get; set; }
}
