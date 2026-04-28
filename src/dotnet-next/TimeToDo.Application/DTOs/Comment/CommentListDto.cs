namespace TimeToDo.Application.DTOs.Comment;

public class CommentListDto
{
    public required string Id { get; set; }
    public string? Description { get; set; }

    public RefPropertyDto? CreatedBy { get; set; }

    public RefPropertyDto? UpdatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public bool IHaveRead { get; set; }
}
