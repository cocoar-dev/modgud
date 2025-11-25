using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Services;
using Cocoar.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers.Admin;

[Route("api/admin/roles")]
[Authorize(Roles = "Admin")]
public class RolesAdminController : ApiControllerBase
{
    private readonly RoleService _roleService;

    public RolesAdminController(RoleService roleService)
    {
        _roleService = roleService;
    }

    /// <summary>
    /// Get all roles.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(RoleListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken)
    {
        var result = await _roleService.GetAllAsync(cancellationToken);
        return FromErrorOr(result);
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

        var result = await _roleService.GetByIdAsync(roleId, cancellationToken);
        return FromErrorOr(result);
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
        var result = await _roleService.CreateAsync(dto, cancellationToken);
        return FromErrorOr(result, role => CreatedAtAction(nameof(GetRole), new { id = role.Id.ToString() }, role));
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

        var result = await _roleService.UpdateAsync(roleId, dto, cancellationToken);
        return FromErrorOr(result);
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

        var result = await _roleService.DeleteAsync(roleId, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }
}
