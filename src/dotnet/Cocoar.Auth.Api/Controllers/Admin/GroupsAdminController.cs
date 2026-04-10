using Cocoar.Auth.Api.Extensions;
using Cocoar.Auth.Api.Hubs;
using Cocoar.Auth.Application.Commands.Groups;
using Cocoar.Auth.Application.DTOs.Groups;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Application.Queries.Groups;
using Cocoar.Auth.Application.ReadModels;
using Cocoar.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Cocoar.Auth.Api.Controllers.Admin;

[Route("api/admin/groups")]
[Authorize(Roles = "Admin")]
public class GroupsAdminController : ApiControllerBase
{
	private readonly IMessageBus _messageBus;
	private readonly IAdminHubNotifier _hubNotifier;

	public GroupsAdminController(IMessageBus messageBus, IAdminHubNotifier hubNotifier)
	{
		_messageBus = messageBus;
		_hubNotifier = hubNotifier;
	}

	[HttpGet]
	[ProducesResponseType(typeof(GroupListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetGroups(CancellationToken ct)
	{
		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<IReadOnlyList<GroupListReadModel>>>(
			GetTenantId(), new GetAllGroupsQuery(), ct);

		return result.Match(
			groups => Ok(new GroupListDto
			{
				Items = groups.Select(GroupMapper.ToListDto).ToList(),
				TotalCount = groups.Count
			}),
			errors => Problem(errors));
	}

	[HttpGet("{id}")]
	[ProducesResponseType(typeof(GroupDetailDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetGroup(string id, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId))
			return BadRequest("Invalid group ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<GroupState>>(
			GetTenantId(), new GetGroupByIdQuery(groupId), ct);

		return result.Match(
			group => Ok(GroupMapper.ToDetailDto(group)),
			errors => Problem(errors));
	}

	[HttpPost]
	[ProducesResponseType(typeof(GroupDetailDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	public async Task<IActionResult> CreateGroup([FromBody] CreateGroupDto dto, CancellationToken ct)
	{
		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<GroupDetailDto>>(
			GetTenantId(), dto.ToCommand(), ct);

		if (result.IsError) return Problem(result.Errors);

		var group = result.Value;
		await _hubNotifier.EntityChangedAsync("group", "created", group.Id.ToString());
		return CreatedAtAction(nameof(GetGroup), new { id = group.Id.ToString() }, group);
	}

	[HttpPatch("{id}")]
	[ProducesResponseType(typeof(GroupDetailDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateGroup(string id, [FromBody] UpdateGroupDto dto, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId))
			return BadRequest("Invalid group ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<GroupDetailDto>>(
			GetTenantId(), dto.ToCommand(groupId), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return Ok(result.Value);
	}

	[HttpDelete("{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> ArchiveGroup(string id, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId))
			return BadRequest("Invalid group ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new ArchiveGroupCommand(groupId), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "deleted", id);
		return NoContent();
	}

	// ── Membership ──

	[HttpPost("{id}/members")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> AddMember(string id, [FromBody] AddGroupMemberDto dto, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId))
			return BadRequest("Invalid group ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new AddGroupMemberCommand(groupId, dto.UserId.Guid), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return NoContent();
	}

	[HttpDelete("{id}/members/{userId}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> RemoveMember(string id, string userId, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId) || !ShortGuid.TryParse(userId, out Guid uid))
			return BadRequest("Invalid ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new RemoveGroupMemberCommand(groupId, uid), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return NoContent();
	}

	// ── Nesting ──

	[HttpPost("{id}/children")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> AddChildGroup(string id, [FromBody] AddChildGroupDto dto, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId))
			return BadRequest("Invalid group ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new AddChildGroupCommand(groupId, dto.ChildGroupId.Guid), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return NoContent();
	}

	[HttpDelete("{id}/children/{childId}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> RemoveChildGroup(string id, string childId, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId) || !ShortGuid.TryParse(childId, out Guid cid))
			return BadRequest("Invalid ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new RemoveChildGroupCommand(groupId, cid), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return NoContent();
	}

	// ── Role Grants ──

	[HttpPost("{id}/roles")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> GrantRole(string id, [FromBody] GrantRoleDto dto, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId))
			return BadRequest("Invalid group ID format.");

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new GrantGroupRoleCommand(groupId, dto.RoleId.Guid, dto.ClientId?.Guid), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return NoContent();
	}

	[HttpDelete("{id}/roles/{roleId}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> RevokeRole(string id, string roleId, [FromQuery] string? clientId, CancellationToken ct)
	{
		if (!ShortGuid.TryParse(id, out Guid groupId) || !ShortGuid.TryParse(roleId, out Guid rid))
			return BadRequest("Invalid ID format.");

		Guid? cid = !string.IsNullOrEmpty(clientId) && ShortGuid.TryParse(clientId, out Guid parsedCid)
			? parsedCid
			: null;

		var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
			GetTenantId(), new RevokeGroupRoleCommand(groupId, rid, cid), ct);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("group", "updated", id);
		return NoContent();
	}
}
