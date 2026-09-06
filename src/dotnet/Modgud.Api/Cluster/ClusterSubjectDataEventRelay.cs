using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Server;

namespace Modgud.Api.Cluster;

/// <summary>
/// Cross-node transport for <see cref="DataEvent"/>s (ADR 0022, D5): a thin
/// adapter between the in-process <see cref="DataEventDispatcher"/> and a
/// SignalARRR cluster subject on the backplane.
/// <para>
/// Every hub in Modgud is a server stream fed by the dispatcher's observable,
/// not a targeted send, so the backplane alone would route nothing. The cluster
/// subject relays each locally raised event to the same-named subject on the
/// other nodes; this adapter feeds it from the dispatcher and replays what the
/// peers raised back into the dispatcher. A node recognises its own envelopes
/// by node id and skips them, so each browser sees every event exactly once,
/// from the node its connection is pinned to. Delivery, ordering and catch-up
/// after a listener drop are the backplane's.
/// </para>
/// </summary>
public sealed class ClusterSubjectDataEventRelay : IDataEventRelay, IHostedService
{
    public const string SubjectName = "modgud-data-events";

    private readonly IClusterSubject<DataEventEnvelope> _subject;
    private readonly string _nodeId;
    private readonly Func<DataEventDispatcher> _dispatcher;
    private readonly ILogger<ClusterSubjectDataEventRelay> _logger;
    private IDisposable? _subscription;

    public ClusterSubjectDataEventRelay(
        IClusterSubject<DataEventEnvelope> subject,
        string nodeId,
        Func<DataEventDispatcher> dispatcher,
        ILogger<ClusterSubjectDataEventRelay> logger)
    {
        _subject = subject;
        _nodeId = nodeId;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
    {
        // Local subscribers were notified by the dispatcher already; the subject's
        // OnNext is local-now (which this node ignores by id) and relay-later.
        _subject.OnNext(DataEventEnvelope.Encode(@event, _nodeId));
        return ValueTask.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _subject.Subscribe(OnEnvelope);
        _logger.LogInformation("Data-event relay started on cluster subject {Subject}, node {Node}", SubjectName, _nodeId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnEnvelope(DataEventEnvelope envelope)
    {
        try
        {
            var dataEvent = DataEventEnvelope.Decode(envelope, _nodeId);
            if (dataEvent is null) return;
            _dispatcher().DispatchRemoteEvent(dataEvent);
        }
        catch (Exception ex)
        {
            // A peer on another build may send a shape we cannot read; the grid
            // catches up on its next fetch. Never let one event kill the subscription.
            _logger.LogWarning(ex, "Dropping a relayed data event ({Subject}) that could not be rehydrated", envelope.Subject);
        }
    }
}
