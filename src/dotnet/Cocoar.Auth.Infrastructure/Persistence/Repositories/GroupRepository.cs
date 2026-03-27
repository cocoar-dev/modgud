using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Aggregates;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Repositories;

public class GroupRepository : IGroupRepository
{
	private readonly IDocumentSession _session;

	public GroupRepository(IDocumentSession session)
	{
		_session = session;
	}

	public async Task<GroupState?> LoadStateAsync(Guid id, CancellationToken ct = default)
	{
		return await _session.LoadAsync<GroupState>(id, ct);
	}

	public async Task<IReadOnlyList<GroupState>> QueryAllActiveStatesAsync(CancellationToken ct = default)
	{
		return await _session
			.Query<GroupState>()
			.Where(g => !g.IsArchived)
			.ToListAsync(ct);
	}

	public async Task StartStreamAsync(Guid id, object @event, CancellationToken ct = default)
	{
		_session.Events.StartStream<GroupAggregate>(id, @event);
		await _session.SaveChangesAsync(ct);
	}

	public async Task AppendEventsAsync(Guid id, object[] events, CancellationToken ct = default)
	{
		if (events.Length == 0) return;
		_session.Events.Append(id, events);
		await _session.SaveChangesAsync(ct);
	}

	public async Task<GroupState?> ReloadStateAsync(Guid id, CancellationToken ct = default)
	{
		// Eject cached version from identity map to force fresh load
		var cached = await _session.LoadAsync<GroupState>(id, ct);
		if (cached is not null)
			_session.Eject(cached);

		return await _session.LoadAsync<GroupState>(id, ct);
	}
}
