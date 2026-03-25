namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Appends domain events to an event stream.
/// Abstracts the event store so Application layer doesn't depend on Marten.
/// </summary>
public interface IEventAppender
{
	Task AppendAsync(Guid streamId, object @event, CancellationToken ct = default);
}
