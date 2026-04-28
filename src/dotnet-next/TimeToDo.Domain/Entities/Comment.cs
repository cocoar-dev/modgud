using ErrorOr;
using TimeToDo.Domain.Errors;

namespace TimeToDo.Domain.Entities;

/// <summary>
/// Simple domain entity for Comment.
/// Can be attached to different entity types (polymorphic).
/// </summary>
public class Comment
{
    public Guid Id { get; private set; }
    public string Description { get; private set; } = string.Empty;

    public Guid ReferencedItemId { get; private set; }
    public string ReferencedItemType { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }
    public Guid CreatedById { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public Guid? UpdatedById { get; private set; }

    private Comment() { }

    /// <summary>
    /// Factory method to create a new Comment with validation.
    /// </summary>
    public static ErrorOr<Comment> Create(
        string description,
        Guid referencedItemId,
        string referencedItemType,
        Guid createdById,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            return DomainErrors.Comment.DescriptionRequired;

        if (referencedItemId == Guid.Empty || string.IsNullOrWhiteSpace(referencedItemType))
            return DomainErrors.Comment.ReferencedItemRequired;

        return new Comment
        {
            Id = id ?? Guid.NewGuid(),
            Description = description,
            ReferencedItemId = referencedItemId,
            ReferencedItemType = referencedItemType,
            CreatedAt = DateTime.UtcNow,
            CreatedById = createdById
        };
    }

    /// <summary>
    /// Reconstitutes a Comment from persistence data.
    /// </summary>
    public static Comment Reconstitute(
        Guid id,
        string description,
        Guid referencedItemId,
        string referencedItemType,
        DateTime createdAt,
        Guid createdById,
        DateTime? updatedAt,
        Guid? updatedById)
    {
        return new Comment
        {
            Id = id,
            Description = description,
            ReferencedItemId = referencedItemId,
            ReferencedItemType = referencedItemType,
            CreatedAt = createdAt,
            CreatedById = createdById,
            UpdatedAt = updatedAt,
            UpdatedById = updatedById
        };
    }

    /// <summary>
    /// Updates the Comment's description.
    /// </summary>
    public ErrorOr<Success> Update(string description, Guid updatedById)
    {
        if (string.IsNullOrWhiteSpace(description))
            return DomainErrors.Comment.DescriptionRequired;

        Description = description;
        UpdatedAt = DateTime.UtcNow;
        UpdatedById = updatedById;

        return Result.Success;
    }
}
