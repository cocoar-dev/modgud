using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record RemoveGroupMemberCommand(Guid GroupId, Guid UserId);

public class RemoveGroupMemberHandler
{
	private readonly IGroupRepository _repository;

	public RemoveGroupMemberHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(RemoveGroupMemberCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		if (!group.MemberIds.Contains(command.UserId))
			return GroupErrors.MemberNotFound(command.UserId);

		await _repository.AppendEventsAsync(command.GroupId,
			[new GroupMemberRemoved(command.GroupId, command.UserId)], cancellationToken);

		return true;
	}
}
