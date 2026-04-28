using BuildingBlocks.Helper;
using Riok.Mapperly.Abstractions;
using TimeToDo.Application.DTOs.User;
using TimeToDo.Domain.Entities;

namespace TimeToDo.Application.Mappers;

[Mapper]
public static partial class UserMapper
{
    public static UserDto ToDto(this User entity)
    {
        return new UserDto
        {
            Id = new ShortGuid(entity.Id).ToString(),
            Firstname = entity.Firstname ?? string.Empty,
            Lastname = entity.Lastname ?? string.Empty,
            Acronym = entity.Acronym,
            Email = entity.Email
        };
    }
}
