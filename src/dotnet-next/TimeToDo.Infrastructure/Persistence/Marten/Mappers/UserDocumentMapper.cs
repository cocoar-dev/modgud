using BuildingBlocks.Helper;
using TimeToDo.Application.DTOs.User;
using TimeToDo.Domain.Entities;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

/// <summary>
/// Maps between User domain entity and UserDocument persistence model.
/// </summary>
public static class UserDocumentMapper
{
    public static User ToDomainEntity(this UserDocument document)
    {
        return User.Reconstitute(
            id: document.Id,
            firstname: document.Firstname,
            lastname: document.Lastname,
            acronym: document.Acronym,
            email: document.Email
        );
    }

    public static UserDocument ToDocument(this User entity)
    {
        return new UserDocument
        {
            Id = entity.Id,
            Firstname = entity.Firstname,
            Lastname = entity.Lastname,
            Acronym = entity.Acronym,
            Email = entity.Email
        };
    }

    public static void UpdateFromEntity(this UserDocument document, User entity)
    {
        document.Firstname = entity.Firstname;
        document.Lastname = entity.Lastname;
        document.Acronym = entity.Acronym;
        document.Email = entity.Email;
    }

    // Document → DTO mapping for API handlers
    public static UserDto ToDto(this UserDocument document)
    {
        return new UserDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Firstname = document.Firstname,
            Lastname = document.Lastname,
            Acronym = document.Acronym,
            Email = document.Email
        };
    }
}
