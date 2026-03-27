using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Events;
using ErrorOr;

namespace Cocoar.Auth.Application.Commands.Groups;

public record AddChildGroupCommand(Guid GroupId, Guid ChildGroupId);

public class AddChildGroupHandler
{
	private readonly IGroupRepository _repository;

	public AddChildGroupHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<bool>> HandleAsync(AddChildGroupCommand command, CancellationToken cancellationToken)
	{
		if (command.ChildGroupId == command.GroupId)
			return GroupErrors.CannotBeSelfChild;

		var group = await _repository.LoadStateAsync(command.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(command.GroupId);

		if (group.ChildGroupIds.Contains(command.ChildGroupId))
			return GroupErrors.ChildAlreadyExists(command.ChildGroupId);

		// Cycle detection: walk descendants of the proposed child to check
		// if we can reach the parent group (which would create a cycle).
		if (await WouldCreateCycleAsync(command.GroupId, command.ChildGroupId, cancellationToken))
			return GroupErrors.CycleDetected;

		await _repository.AppendEventsAsync(command.GroupId,
			[new GroupChildAdded(command.GroupId, command.ChildGroupId)], cancellationToken);

		return true;
	}

	private async Task<bool> WouldCreateCycleAsync(Guid parentId, Guid childId, CancellationToken ct)
	{
		var allGroups = await _repository.QueryAllActiveStatesAsync(ct);
		var childMap = allGroups.ToDictionary(g => g.Id, g => g.ChildGroupIds);

		// BFS from childId through existing children
		var visited = new HashSet<Guid>();
		var queue = new Queue<Guid>();
		queue.Enqueue(childId);

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (current == parentId)
				return true;

			if (!visited.Add(current))
				continue;

			if (childMap.TryGetValue(current, out var children))
			{
				foreach (var child in children)
					queue.Enqueue(child);
			}
		}

		return false;
	}
}
