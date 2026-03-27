using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record ArchiveGroupCommand(Guid GroupId);

public class ArchiveGroupHandler
{
	private readonly IGroupRepository _repository;

	public ArchiveGroupHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(ArchiveGroupCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null)
			return GroupErrors.NotFound(command.GroupId);

		if (group.IsArchived)
			return GroupErrors.AlreadyArchived(command.GroupId);

		await _repository.AppendEventsAsync(command.GroupId, [new GroupArchived(command.GroupId, null)], cancellationToken);

		return true;
	}
}
