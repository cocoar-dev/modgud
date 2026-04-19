using Cocoar.Auth.Api.Authorization;
using Cocoar.Auth.Api.Extensions;
using Cocoar.Auth.Application.Commands.Roles;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Application.Queries.Roles;
using Cocoar.Auth.Application.ReadModels;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Cocoar.Auth.Api.Controllers.Admin;

[Route("api/admin/roles")]
[RequiresAbacPermission("role:read")]
public class RolesAdminController : ApiControllerBase
{
    private readonly IMessageBus _messageBus;

    public RolesAdminController(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    /// <summary>
    /// Get all roles.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(RoleListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken)
    {
        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<IReadOnlyList<RoleListReadModel>>>(
            GetTenantId(), new GetAllRolesQuery(),
            cancellationToken);

        return result.Match(
            roles => Ok(new RoleListDto
            {
                Items = roles.Select(RoleMapper.ToListDto).ToList(),
                TotalCount = roles.Count
            }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Get a role by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRole(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid roleId))
        {
            return BadRequest("Invalid role ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<ApplicationRole>>(
            GetTenantId(), new GetRoleByIdQuery(roleId),
            cancellationToken);

        return result.Match(
            role => Ok(RoleMapper.ToDto(role)),
            errors => Problem(errors));
    }

    /// <summary>
    /// Create a new role.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleDto dto, CancellationToken cancellationToken)
    {
        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<ApplicationRole>>(
            GetTenantId(), dto.ToCommand(),
            cancellationToken);

        if (result.IsError) return Problem(result.Errors);

        var role = result.Value;
        return CreatedAtAction(nameof(GetRole), new { id = role.Id.ToString() }, RoleMapper.ToDto(role));
    }

    /// <summary>
    /// Update an existing role.
    /// </summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRole(string id, [FromBody] UpdateRoleDto dto, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid roleId))
        {
            return BadRequest("Invalid role ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<ApplicationRole>>(
            GetTenantId(), dto.ToCommand(roleId),
            cancellationToken);

        if (result.IsError) return Problem(result.Errors);

        var role = result.Value;
        return Ok(RoleMapper.ToDto(role));
    }

    /// <summary>
    /// Delete a role.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRole(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid roleId))
        {
            return BadRequest("Invalid role ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
            GetTenantId(), new DeleteRoleCommand(roleId),
            cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }
}
