using Cocoar.Auth.Application.DTOs.Groups;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Application.ReadModels;
using Cocoar.Primitives;

namespace Cocoar.Auth.Application.Mappers;

public static class GroupMapper
{
	public static GroupDetailDto ToDetailDto(GroupState group) => new()
	{
		Id = new ShortGuid(group.Id),
		Name = group.Name,
		Description = group.Description,
		IsArchived = group.IsArchived,
		MemberIds = group.MemberIds.Select(id => new ShortGuid(id)).ToList(),
		ChildGroupIds = group.ChildGroupIds.Select(id => new ShortGuid(id)).ToList(),
		RealmRoleGrants = group.RealmRoleGrants
			.Select(g => new GroupRealmRoleGrantDto(new ShortGuid(g.RoleId))).ToList(),
		ClientRoleGrants = group.ClientRoleGrants
			.Select(g => new GroupClientRoleGrantDto(new ShortGuid(g.RoleId), new ShortGuid(g.ClientId))).ToList(),
		CreatedAt = group.CreatedAt,
		ModifiedAt = group.ModifiedAt,
	};

	public static GroupDto ToListDto(GroupListReadModel g) => new()
	{
		Id = new ShortGuid(g.Id),
		Name = g.Name,
		Description = g.Description,
		IsArchived = g.IsArchived,
		MemberCount = g.MemberCount,
		ChildGroupCount = g.ChildGroupCount,
		RoleGrantCount = g.RoleGrantCount,
		CreatedAt = g.CreatedAt,
		ModifiedAt = g.ModifiedAt,
	};
}
