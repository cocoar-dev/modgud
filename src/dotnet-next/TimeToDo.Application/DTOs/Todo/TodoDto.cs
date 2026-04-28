using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Application.DTOs.Todo;

public class TodoDto
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime? DueDate { get; set; }
    public required TodoStatus Status { get; set; }

    public RefPropertyDto? Customer { get; set; }

    public List<RefPropertyDto> Responsibles { get; set; } = new();
    public string? ParentTodoId { get; set; }

    public RefPropertyDto? CreatedBy { get; set; }

    public RefPropertyDto? UpdatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public bool Critical { get; set; }
    public bool AwaitingFeedback { get; set; }

    public List<Comment.CommentListDto>? Comments { get; set; }

    public bool IsArchived { get; set; }

    public int ChildTodosCount { get; set; }
    public int ChildTodosUnreadCommentsCount { get; set; }

    public DateTime LastTouchedAt { get; set; }

    public int UnreadComments { get; set; }
    public int CommentsCount { get; set; }

    public long AggregateVersion { get; set; }

    public EntityStatus EntityStatus { get; set; } = EntityStatus.Active;
}
