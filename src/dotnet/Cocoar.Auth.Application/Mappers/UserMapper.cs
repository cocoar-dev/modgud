using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Application.ReadModels;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Riok.Mapperly.Abstractions;

namespace Cocoar.Auth.Application.Mappers;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class UserMapper
{
    // Map from ApplicationUser (for internal Identity operations)
    public static partial UserDto ToDto(ApplicationUser user);

    // Map from UserDetailsReadModel (for detail view API responses)
    public static partial UserDto ToDto(UserDetailsReadModel user);

    // Map from UserListReadModel (for list view API responses)
    public static UserDto ToListDto(UserListReadModel user) => new()
    {
        Id = new ShortGuid(user.Id),
        UserName = user.UserName,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        IsActive = user.IsActive,
        TwoFactorEnabled = user.TwoFactorEnabled,
        LockoutEnd = user.LockoutEnd,
        CreatedAt = user.CreatedAt,
        ModifiedAt = user.ModifiedAt,
        Roles = user.Roles.Select(r => new ShortGuid(r.Id)).ToList(),
    };

    private static ShortGuid MapIdToShortGuid(Guid id) => new ShortGuid(id);

    private static List<ShortGuid> MapRolesToShortGuids(List<Guid> roles) =>
        roles.Select(r => new ShortGuid(r)).ToList();

    private static List<ShortGuid> MapRoleInfoToShortGuids(List<RoleInfo> roles) =>
        roles.Select(r => new ShortGuid(r.Id)).ToList();

    private static List<UserClaimDto> MapUserClaimsToDto(List<UserClaim> claims) =>
        claims.Select(c => new UserClaimDto { Type = c.Type, Value = c.Value }).ToList();

    private static List<UserClaimDto> MapClaimInfoToDto(List<ClaimInfo> claims) =>
        claims.Select(c => new UserClaimDto { Type = c.Type, Value = c.Value }).ToList();
}
