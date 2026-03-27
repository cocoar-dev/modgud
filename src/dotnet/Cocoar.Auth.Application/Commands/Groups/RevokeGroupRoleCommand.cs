using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record RevokeGroupRoleCommand(Guid GroupId, Guid RoleId, Guid? ClientId);

public class RevokeGroupRoleHandler
{
	private readonly IGroupRepository _repository;

	public RevokeGroupRoleHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(RevokeGroupRoleCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		object @event = command.ClientId.HasValue
			? new GroupClientRoleRevoked(command.GroupId, command.RoleId, command.ClientId.Value)
			: new GroupRealmRoleRevoked(command.GroupId, command.RoleId);

		await _repository.AppendEventsAsync(command.GroupId, [@event], cancellationToken);

		return true;
	}
}
