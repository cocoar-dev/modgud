namespace BuildingBlocks.EventDispatcher;

/// <summary>
/// Carries locally raised <see cref="DataEvent"/>s to every other node of a
/// multi-instance deployment so that a subscriber connected to node B sees an
/// event produced by a request on node A. The dispatcher's observable is
/// process-local by nature; this seam is what makes it cluster-wide.
/// <para>
/// Implementations must be fire-and-forget from the dispatcher's point of view:
/// a slow or failing relay must never block or fail the local dispatch.
/// Inbound events from peers are handed back through
/// <see cref="DataEventDispatcher.DispatchRemoteEvent"/>, which does not relay
/// them again.
/// </para>
/// </summary>
public interface IDataEventRelay
{
    /// <summary>Publishes a locally raised event to the other nodes.</summary>
    ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>Single-node default: nothing to relay to.</summary>
public sealed class NoDataEventRelay : IDataEventRelay
{
    public static readonly NoDataEventRelay Instance = new();

    public ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
