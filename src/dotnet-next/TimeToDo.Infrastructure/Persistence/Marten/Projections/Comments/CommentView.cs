using Marten.Schema;
using TimeToDo.Infrastructure.Persistence.Marten.Projections;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;

[DocumentAlias("comment_view")]
public record CommentView
{
    public Guid Id { get; init; }
    public string Description { get; init; } = string.Empty;
    public Guid ReferencedItemId { get; init; }
    public string ReferencedItemType { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public ViewRef? CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public Guid? UpdatedById { get; init; }
    public bool IsDeleted { get; init; }
}
