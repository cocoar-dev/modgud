using Cocoar.Auth.Application.DTOs.Groups;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using Cocoar.Primitives;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record CreateGroupCommand(string Name, string? Description);

public class CreateGroupHandler
{
	private readonly IGroupRepository _repository;

	public CreateGroupHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<GroupDetailDto>> HandleAsync(CreateGroupCommand command, CancellationToken cancellationToken)
	{
		var groupId = Guid.CreateVersion7();
		var @event = new GroupCreated(groupId, command.Name, command.Description);

		await _repository.StartStreamAsync(groupId, @event, cancellationToken);

		return new GroupDetailDto
		{
			Id = new ShortGuid(groupId),
			Name = command.Name,
			Description = command.Description,
			CreatedAt = DateTimeOffset.UtcNow,
		};
	}
}
