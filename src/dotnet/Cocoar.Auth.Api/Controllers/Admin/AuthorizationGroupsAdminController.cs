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

[Route("api/admin/authorization-groups")]
[RequiresAbacPermission("authorization-group:read")]
public class AuthorizationGroupsAdminController : ApiControllerBase
{
    private readonly IMessageBus _messageBus;
    private readonly ITenantSessionFactory _sessionFactory;

    public AuthorizationGroupsAdminController(IMessageBus messageBus, ITenantSessionFactory sessionFactory)
    {
        _messageBus = messageBus;
        _sessionFactory = sessionFactory;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<AuthorizationGroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var groups = await session.Query<AuthorizationGroup>()
            .Where(g => !g.IsDeleted)
            .ToListAsync(ct);

        return Ok(groups.Select(Map).ToList());
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(AuthorizationGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(string id, CancellationToken ct)
    {
        if (!ShortGuid.TryParse(id, out Guid groupId))
            return BadRequest("Invalid group ID.");

        await using var session = _sessionFactory.OpenQuerySession();
        var group = await session.LoadAsync<AuthorizationGroup>(groupId, ct);
        if (group is null || group.IsDeleted) return NotFound();

        return Ok(Map(group));
    }

    [HttpPost]
    [RequiresAbacPermission("authorization-group:create")]
    [ProducesResponseType(typeof(AuthorizationGroupDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateAuthorizationGroupInput input, CancellationToken ct)
    {
        var result = await _messageBus.InvokeForTenantAsync<ErrorOr<AuthorizationGroupDto>>(
            GetTenantId(), new CreateAuthorizationGroupCommand(input), ct);
        return FromErrorOr(result, dto => CreatedAtAction(nameof(GetOne), new { id = dto.Id.ToString() }, dto));
    }

    [HttpPut("{id}")]
    [RequiresAbacPermission("authorization-group:update")]
    [ProducesResponseType(typeof(AuthorizationGroupDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateAuthorizationGroupInput input, CancellationToken ct)
    {
        if (!ShortGuid.TryParse(id, out Guid groupId))
            return BadRequest("Invalid group ID.");

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr<AuthorizationGroupDto>>(
            GetTenantId(), new UpdateAuthorizationGroupCommand(groupId, input), ct);
        return FromErrorOr(result);
    }

    [HttpDelete("{id}")]
    [RequiresAbacPermission("authorization-group:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!ShortGuid.TryParse(id, out Guid groupId))
            return BadRequest("Invalid group ID.");

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr<Deleted>>(
            GetTenantId(), new DeleteAuthorizationGroupCommand(groupId), ct);
        return result.IsError ? Problem(result.Errors) : NoContent();
    }

    private static AuthorizationGroupDto Map(AuthorizationGroup g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Description = g.Description,
        MemberIds = g.MemberIds,
        RoleIds = g.RoleIds,
        AccessScripts = g.AccessScripts.Select(s => new ResourceAccessScriptDto
        {
            ResourceType = s.ResourceType,
            Script = s.Script,
        }).ToList(),
        MembershipMode = g.MembershipMode,
        MembershipScript = g.MembershipScript,
        MembershipScriptDependencies = g.MembershipScriptDependencies,
        MembershipLastError = g.MembershipLastError,
        Email = g.Email,
        EmailMode = g.EmailMode,
    };
}
