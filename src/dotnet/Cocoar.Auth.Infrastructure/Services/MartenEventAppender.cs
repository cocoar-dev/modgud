using Cocoar.Auth.Application.Interfaces;
using Marten;

namespace Cocoar.Auth.Infrastructure.Services;

public class MartenEventAppender : IEventAppender
{
	private readonly IDocumentSession _session;

	public MartenEventAppender(IDocumentSession session)
	{
		_session = session;
	}

	public async Task AppendAsync(Guid streamId, object @event, CancellationToken ct = default)
	{
		_session.Events.Append(streamId, @event);
		await _session.SaveChangesAsync(ct);
	}
}
