using TimeToDo.Application.DTOs.Comment;

namespace TimeToDo.Application.Contracts;

/// <summary>
/// Query service for retrieving Comment DTOs.
/// </summary>
public interface ICommentQueryService
{
    Task<IReadOnlyList<CommentListDto>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CommentListDto>> GetByReferenceAsync(
        string referenceType,
        Guid referenceId,
        Guid? currentUserId = null,
        CancellationToken ct = default);
    Task<CommentListDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
