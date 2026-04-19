using Cocoar.Auth.Api.Authorization;
using Cocoar.Auth.Application.Commands.Authorization;
using Cocoar.Auth.Application.DTOs.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Primitives;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Cocoar.Auth.Api.Controllers.Admin;

[Route("api/admin/permission-roles")]
[RequiresAbacPermission("permission-role:read")]
public class PermissionRolesAdminController : ApiControllerBase
{
    private readonly IMessageBus _messageBus;
    private readonly ITenantSessionFactory _sessionFactory;

    public PermissionRolesAdminController(IMessageBus messageBus, ITenantSessionFactory sessionFactory)
    {
        _messageBus = messageBus;
        _sessionFactory = sessionFactory;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<PermissionRoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var roles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted)
            .ToListAsync(ct);

        return Ok(roles.Select(Map).ToList());
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PermissionRoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(string id, CancellationToken ct)
    {
        if (!ShortGuid.TryParse(id, out Guid roleId))
            return BadRequest("Invalid role ID.");

        await using var session = _sessionFactory.OpenQuerySession();
        var role = await session.LoadAsync<PermissionRole>(roleId, ct);
        if (role is null || role.IsDeleted) return NotFound();

        return Ok(Map(role));
    }

    [HttpPost]
    [RequiresAbacPermission("permission-role:create")]
    [ProducesResponseType(typeof(PermissionRoleDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreatePermissionRoleInput input, CancellationToken ct)
    {
        var result = await _messageBus.InvokeForTenantAsync<ErrorOr<PermissionRoleDto>>(
            GetTenantId(), new CreatePermissionRoleCommand(input), ct);
        return FromErrorOr(result, dto => CreatedAtAction(nameof(GetOne), new { id = dto.Id.ToString() }, dto));
    }

    [HttpPut("{id}")]
    [RequiresAbacPermission("permission-role:update")]
    [ProducesResponseType(typeof(PermissionRoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdatePermissionRoleInput input, CancellationToken ct)
    {
        if (!ShortGuid.TryParse(id, out Guid roleId))
            return BadRequest("Invalid role ID.");

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr<PermissionRoleDto>>(
            GetTenantId(), new UpdatePermissionRoleCommand(roleId, input), ct);
        return FromErrorOr(result);
    }

    [HttpDelete("{id}")]
    [RequiresAbacPermission("permission-role:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!ShortGuid.TryParse(id, out Guid roleId))
            return BadRequest("Invalid role ID.");

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr<Deleted>>(
            GetTenantId(), new DeletePermissionRoleCommand(roleId), ct);
        return result.IsError ? Problem(result.Errors) : NoContent();
    }

    private static PermissionRoleDto Map(PermissionRole r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Description = r.Description,
        ResourceType = r.ResourceType,
        Permissions = r.Permissions,
    };
}
