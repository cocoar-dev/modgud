using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Groups;

public record GetGroupByIdQuery(Guid GroupId);

public class GetGroupByIdHandler
{
	private readonly IGroupRepository _repository;

	public GetGroupByIdHandler(IGroupRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<GroupState>> HandleAsync(GetGroupByIdQuery query, CancellationToken cancellationToken)
	{
		var group = await _repository.LoadStateAsync(query.GroupId, cancellationToken);
		if (group is null || group.IsArchived)
			return GroupErrors.NotFound(query.GroupId);

		return group;
	}
}
