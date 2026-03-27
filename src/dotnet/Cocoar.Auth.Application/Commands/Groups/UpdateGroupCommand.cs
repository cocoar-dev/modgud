using Cocoar.Auth.Application.DTOs.Groups;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record UpdateGroupCommand(Guid GroupId, string? Name, string? Description);

public class UpdateGroupHandler
{
	private readonly IGroupRepository _repository;

	public UpdateGroupHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<GroupDetailDto>> HandleAsync(UpdateGroupCommand command, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		var events = new List<object>();

		if (command.Name is not null && command.Name != group.Name)
		{
			events.Add(new GroupRenamed(command.GroupId, group.Name, command.Name));
			group.Name = command.Name;
		}

		if (command.Description is not null && command.Description != group.Description)
		{
			events.Add(new GroupDescriptionChanged(command.GroupId, group.Description, command.Description));
			group.Description = command.Description;
		}

		if (events.Count > 0)
		{
			group.ModifiedAt = DateTimeOffset.UtcNow;
			await _repository.AppendEventsAsync(command.GroupId, events.ToArray(), cancellationToken);
		}

		return GroupMapper.ToDetailDto(group);
	}
}
