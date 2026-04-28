using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

public static class CommentViewMapper
{
    public static CommentListDto ToListDto(this CommentView view)
    {
        return new CommentListDto
        {
            Id = new ShortGuid(view.Id).ToString(),
            Description = view.Description,
            CreatedAt = view.CreatedAt,
            CreatedBy = view.CreatedBy != null
                ? new RefPropertyDto
                {
                    Id = new ShortGuid(view.CreatedBy.Id).ToString(),
                    Label = view.CreatedBy.Label,
                    PrincipalType = view.CreatedBy.PrincipalType,
                }
                : null,
            UpdatedAt = view.UpdatedAt,
            IHaveRead = false
        };
    }

    public static async Task<CommentListDto> ToListDtoEnrichedAsync(
        this CommentView view,
        IDocumentSession session,
        Guid? currentUserId = null,
        CancellationToken ct = default)
    {
        var dto = view.ToListDto();

        if (currentUserId.HasValue)
        {
            dto.IHaveRead = await session.Query<CommentReadStatus>()
                .AnyAsync(rs => rs.CommentId == view.Id && rs.UserId == currentUserId.Value, ct);
        }

        return dto;
    }
}
