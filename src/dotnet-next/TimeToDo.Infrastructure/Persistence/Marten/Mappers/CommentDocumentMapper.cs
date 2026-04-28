using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Domain.Entities;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

/// <summary>
/// Maps between Comment domain entity and CommentDocument persistence model.
/// </summary>
public static class CommentDocumentMapper
{
    public static Comment ToDomainEntity(this CommentDocument document)
    {
        return Comment.Reconstitute(
            id: document.Id,
            description: document.Description,
            referencedItemId: document.ReferencedItemId,
            referencedItemType: document.ReferencedItemType,
            createdAt: document.CreatedAt,
            createdById: document.CreatedById,
            updatedAt: document.UpdatedAt,
            updatedById: document.UpdatedById
        );
    }

    public static CommentDocument ToDocument(this Comment entity)
    {
        return new CommentDocument
        {
            Id = entity.Id,
            Description = entity.Description,
            ReferencedItemId = entity.ReferencedItemId,
            ReferencedItemType = entity.ReferencedItemType,
            CreatedAt = entity.CreatedAt,
            CreatedById = entity.CreatedById,
            UpdatedAt = entity.UpdatedAt,
            UpdatedById = entity.UpdatedById
        };
    }

    public static void UpdateFromEntity(this CommentDocument document, Comment entity)
    {
        document.Description = entity.Description;
        document.UpdatedAt = entity.UpdatedAt;
        document.UpdatedById = entity.UpdatedById;
    }

    // Document → DTO mapping for API handlers
    public static CommentListDto ToListDto(this CommentDocument document, Guid? currentUserId = null)
    {
        return new CommentListDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Description = document.Description,
            CreatedAt = document.CreatedAt,
            CreatedBy = document.CreatedById != Guid.Empty
                ? new RefPropertyDto { Id = new ShortGuid(document.CreatedById).ToString() }
                : null,
            UpdatedAt = document.UpdatedAt,
            IHaveRead = false // Will be set by enrichment if needed
        };
    }

    public static async Task<CommentListDto> ToListDtoEnrichedAsync(
        this CommentDocument document,
        IDocumentSession session,
        Guid? currentUserId = null,
        CancellationToken ct = default)
    {
        var dto = document.ToListDto(currentUserId);

        // Enrich CreatedBy with user label
        if (dto.CreatedBy != null && document.CreatedById != Guid.Empty)
        {
            var user = await session.LoadAsync<UserView>(document.CreatedById, ct);
            if (user != null)
            {
                dto.CreatedBy.Label = user.GetDisplayLabel();
            }
        }

        // Check read status if user is logged in
        if (currentUserId.HasValue)
        {
            var readStatus = await session.Query<CommentReadStatusDocument>()
                .Where(s => s.CommentId == document.Id && s.UserId == currentUserId.Value)
                .FirstOrDefaultAsync(ct);

            dto.IHaveRead = readStatus != null;
        }

        return dto;
    }

    public static CommentDocument ToDocument(this CommentCreateDto dto, Guid referencedItemId, string referencedItemType, Guid createdById)
    {
        return new CommentDocument
        {
            Id = Guid.NewGuid(),
            Description = dto.Description ?? string.Empty,
            ReferencedItemId = referencedItemId,
            ReferencedItemType = referencedItemType,
            CreatedAt = DateTime.UtcNow,
            CreatedById = createdById,
            UpdatedAt = null,
            UpdatedById = null
        };
    }
}
