using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record RemoveChildGroupCommand(Guid GroupId, Guid ChildGroupId);

public class RemoveChildGroupHandler
{
	private readonly IGroupRepository _repository;

	public RemoveChildGroupHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(RemoveChildGroupCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		if (!group.ChildGroupIds.Contains(command.ChildGroupId))
			return GroupErrors.ChildNotFound(command.ChildGroupId);

		await _repository.AppendEventsAsync(command.GroupId,
			[new GroupChildRemoved(command.GroupId, command.ChildGroupId)], cancellationToken);

		return true;
	}
}
