using BuildingBlocks.Helper;
using Riok.Mapperly.Abstractions;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.Entities;

namespace TimeToDo.Application.Mappers;

[Mapper]
public static partial class TodoMapper
{
    public static TodoDto ToDto(this Todo entity)
    {
        return new TodoDto
        {
            Id = new ShortGuid(entity.Id).ToString(),
            Title = entity.Title,
            Description = entity.Description,
            DueDate = entity.DueDate,
            Status = entity.Status,
            ParentTodoId = entity.ParentTodoId.HasValue ? new ShortGuid(entity.ParentTodoId.Value).ToString() : null,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            Critical = entity.IsCritical,
            AwaitingFeedback = entity.IsAwaitingFeedback,
            IsArchived = entity.IsArchived,
            CommentsCount = entity.CommentsCount,
            ChildTodosCount = entity.ChildTodoIds.Count,
            AggregateVersion = entity.AggregateVersion
        };
    }
}
