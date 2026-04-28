using TimeToDo.Domain.Entities;

namespace TimeToDo.Domain.Repositories;

/// <summary>
/// Repository interface for Comment entity.
/// </summary>
public interface ICommentRepository
{
    /// <summary>
    /// Gets a comment by its ID.
    /// </summary>
    Task<Comment?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all comments for a specific referenced item.
    /// </summary>
    Task<IReadOnlyList<Comment>> GetByReferenceAsync(Guid referencedItemId, string referencedItemType, CancellationToken ct = default);

    /// <summary>
    /// Gets all comments.
    /// </summary>
    Task<IReadOnlyList<Comment>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves a new comment.
    /// </summary>
    Task<Comment> CreateAsync(Comment comment, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing comment.
    /// </summary>
    Task<Comment> UpdateAsync(Comment comment, CancellationToken ct = default);

    /// <summary>
    /// Deletes a comment by its ID.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Marks a comment as read by a user.
    /// </summary>
    Task MarkAsReadAsync(Guid commentId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the list of users who have read a comment.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetReadByUsersAsync(Guid commentId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a user has read a comment.
    /// </summary>
    Task<bool> HasUserReadAsync(Guid commentId, Guid userId, CancellationToken ct = default);
}
