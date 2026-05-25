using BuildingBlocks.Helper;
using Modgud.Application.DTOs.User;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Infrastructure.Persistence.Marten.Mappers;

public static class UserViewMapper
{
    public static UserDto ToDto(this UserView view)
    {
        return new UserDto
        {
            Id = new ShortGuid(view.Id).ToString(),
            Firstname = view.Firstname ?? string.Empty,
            Lastname = view.Lastname ?? string.Empty,
            Acronym = view.Acronym,
            Email = view.Email,
            UserName = view.UserName,
            IsActive = view.IsActive,
            HasPassword = view.HasPassword,
            ExternalLoginProviderIds = view.ExternalLoginProviderIds
                .Select(id => new ShortGuid(id).ToString())
                .ToList(),
        };
    }
}
