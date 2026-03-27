using Cocoar.Primitives;

namespace Cocoar.Auth.Application.DTOs.Groups;

public record GroupDto
{
	public required ShortGuid Id { get; init; }
	public required string Name { get; init; }
	public string? Description { get; init; }
	public bool IsArchived { get; init; }
	public int MemberCount { get; init; }
	public int ChildGroupCount { get; init; }
	public int RoleGrantCount { get; init; }
	public DateTimeOffset CreatedAt { get; init; }
	public DateTimeOffset? ModifiedAt { get; init; }
}

public record CreateGroupDto
{
	public required string Name { get; init; }
	public string? Description { get; init; }
}

public record UpdateGroupDto
{
	public string? Name { get; init; }
	public string? Description { get; init; }
}

public record GroupListDto
{
	public required List<GroupDto> Items { get; init; }
	public required int TotalCount { get; init; }
}

public record AddGroupMemberDto
{
	public required ShortGuid UserId { get; init; }
}

public record AddChildGroupDto
{
	public required ShortGuid ChildGroupId { get; init; }
}

public record GrantRoleDto
{
	public required ShortGuid RoleId { get; init; }
	/// <summary>
	/// Null = realm role grant. Set = client role grant scoped to this client.
	/// </summary>
	public ShortGuid? ClientId { get; init; }
}

/// <summary>
/// Detailed group DTO for the GetById endpoint.
/// Includes full member/child/role arrays for the admin form.
/// </summary>
public record GroupDetailDto
{
	public required ShortGuid Id { get; init; }
	public required string Name { get; init; }
	public string? Description { get; init; }
	public bool IsArchived { get; init; }
	public List<ShortGuid> MemberIds { get; init; } = [];
	public List<ShortGuid> ChildGroupIds { get; init; } = [];
	public List<GroupRealmRoleGrantDto> RealmRoleGrants { get; init; } = [];
	public List<GroupClientRoleGrantDto> ClientRoleGrants { get; init; } = [];
	public DateTimeOffset CreatedAt { get; init; }
	public DateTimeOffset? ModifiedAt { get; init; }
}

public record GroupRealmRoleGrantDto(ShortGuid RoleId);
public record GroupClientRoleGrantDto(ShortGuid RoleId, ShortGuid ClientId);
