using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Riok.Mapperly.Abstractions;

namespace Cocoar.Auth.Application.Mappers;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class UserMapper
{
    public static partial UserDto ToDto(ApplicationUser user);

    private static ShortGuid MapIdToShortGuid(Guid id) => new ShortGuid(id);

    private static List<ShortGuid> MapRolesToShortGuids(List<Guid> roles) =>
        roles.Select(r => new ShortGuid(r)).ToList();
}
