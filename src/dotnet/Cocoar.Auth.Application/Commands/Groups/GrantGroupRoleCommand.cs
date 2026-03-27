using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record GrantGroupRoleCommand(Guid GroupId, Guid RoleId, Guid? ClientId);

public class GrantGroupRoleHandler
{
	private readonly IGroupRepository _repository;

	public GrantGroupRoleHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(GrantGroupRoleCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		object @event;
		if (command.ClientId.HasValue)
		{
			if (group.ClientRoleGrants.Any(g => g.RoleId == command.RoleId && g.ClientId == command.ClientId.Value))
				return GroupErrors.RoleAlreadyGranted(command.RoleId);

			@event = new GroupClientRoleGranted(command.GroupId, command.RoleId, command.ClientId.Value);
		}
		else
		{
			if (group.RealmRoleGrants.Any(g => g.RoleId == command.RoleId))
				return GroupErrors.RoleAlreadyGranted(command.RoleId);

			@event = new GroupRealmRoleGranted(command.GroupId, command.RoleId);
		}

		await _repository.AppendEventsAsync(command.GroupId, [@event], cancellationToken);

		return true;
	}
}
