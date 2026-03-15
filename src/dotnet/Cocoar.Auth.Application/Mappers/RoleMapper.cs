using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Riok.Mapperly.Abstractions;

namespace Cocoar.Auth.Application.Mappers;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class RoleMapper
{
    public static partial RoleDto ToDto(ApplicationRole role);

    private static ShortGuid MapIdToShortGuid(Guid id) => new ShortGuid(id);

    private static ShortGuid? MapNullableGuidToShortGuid(Guid? id) => id.HasValue ? new ShortGuid(id.Value) : (ShortGuid?)null;
}
