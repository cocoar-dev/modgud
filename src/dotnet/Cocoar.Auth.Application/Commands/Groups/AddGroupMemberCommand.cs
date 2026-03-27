using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record AddGroupMemberCommand(Guid GroupId, Guid UserId);

public class AddGroupMemberHandler
{
	private readonly IGroupRepository _repository;

	public AddGroupMemberHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(AddGroupMemberCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		if (group.MemberIds.Contains(command.UserId))
			return GroupErrors.MemberAlreadyExists(command.UserId);

		await _repository.AppendEventsAsync(command.GroupId,
			[new GroupMemberAdded(command.GroupId, command.UserId)], cancellationToken);

		return true;
	}
}
