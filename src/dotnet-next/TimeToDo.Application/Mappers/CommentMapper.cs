using BuildingBlocks.Helper;
using Riok.Mapperly.Abstractions;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Domain.Entities;

namespace TimeToDo.Application.Mappers;

[Mapper]
public static partial class CommentMapper
{
    public static CommentListDto ToListDto(this Comment entity)
    {
        return new CommentListDto
        {
            Id = new ShortGuid(entity.Id).ToString(),
            Description = entity.Description,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }
}
