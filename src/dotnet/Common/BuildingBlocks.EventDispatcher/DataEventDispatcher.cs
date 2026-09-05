using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace BuildingBlocks.EventDispatcher;

public class DataEventDispatcher
{
    private readonly IDataEventRelay _relay;

    public DataEventDispatcher() : this(NoDataEventRelay.Instance) { }

    /// <param name="relay">
    /// Cross-node relay for multi-instance deployments. Every locally raised event
    /// is handed to it after the local subscribers were notified.
    /// </param>
    public DataEventDispatcher(IDataEventRelay relay)
    {
        _relay = relay ?? NoDataEventRelay.Instance;
    }

    private Subject<DataEvent> NotificationsSubject { get; } = new();
    public IObservable<DataEvent> Notifications => NotificationsSubject.AsObservable();

    /// <summary>
    /// Raised when the relay could not publish an event. Observed by the host
    /// for logging; the local dispatch has already happened at that point.
    /// </summary>
    public event Action<DataEvent, Exception>? RelayFailed;

    public void DispatchEvent(DataEvent @event)
    {
        NotificationsSubject.OnNext(@event);
        Relay(@event);
    }

    /// <summary>
    /// Delivers an event received from another node to the local subscribers
    /// without relaying it again.
    /// </summary>
    public void DispatchRemoteEvent(DataEvent @event)
    {
        NotificationsSubject.OnNext(@event);
    }

    private void Relay(DataEvent @event)
    {
        if (ReferenceEquals(_relay, NoDataEventRelay.Instance)) return;

        // Fire-and-forget by contract: the producer's request must not wait on
        // the network, and a relay outage must not turn into a failed command.
        _ = RelayAsync(@event);
    }

    private async Task RelayAsync(DataEvent @event)
    {
        try
        {
            await _relay.PublishAsync(@event).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RelayFailed?.Invoke(@event, ex);
        }
    }

    public void DispatchEvent(DataEventAction action, string subject, IEnumerable<object>? payload = null)
    {
        var ev = new DataEvent(action, subject, payload);
        DispatchEvent(ev);
    }

    public void DispatchCreatedEvent(string subject, object? payload = null)
    {
        DispatchCreatedEvent(subject, ArrayHelper.WrapInArray(payload));
    }
    public void DispatchCreatedEvent(string subject, IEnumerable<object>? payload = null)
    {
        DispatchEvent(DataEvent.Created(subject, payload));
    }

    public void DispatchUpdatedEvent(string subject, object? payload = null)
    {
        DispatchUpdatedEvent(subject, ArrayHelper.WrapInArray(payload));
    }
    public void DispatchUpdatedEvent(string subject, IEnumerable<object>? payload = null)
    {
        DispatchEvent(DataEvent.Updated(subject, payload));
    }

    public void DispatchDeletedEvent(string subject, object? payload = null)
    {
        DispatchDeletedEvent(subject, ArrayHelper.WrapInArray(payload));
    }
    public void DispatchDeletedEvent(string subject, IEnumerable<object>? payload = null)
    {
        DispatchEvent(DataEvent.Deleted(subject, payload));
    }

    // Tenant-scoped overloads. Stamp the originating tenant/partition onto the
    // event so consumers can scope delivery to the matching connection. The
    // payload-only methods above stay for tenant-agnostic / single-tenant use;
    // anything that crosses a tenant boundary MUST go through these.
    public void DispatchCreatedEvent(string subject, object? payload, string? tenant)
    {
        DispatchEvent(DataEvent.Created(subject, ArrayHelper.WrapInArray(payload)).WithTenant(tenant));
    }

    public void DispatchUpdatedEvent(string subject, object? payload, string? tenant)
    {
        DispatchEvent(DataEvent.Updated(subject, ArrayHelper.WrapInArray(payload)).WithTenant(tenant));
    }

    public void DispatchDeletedEvent(string subject, object? payload, string? tenant)
    {
        DispatchEvent(DataEvent.Deleted(subject, ArrayHelper.WrapInArray(payload)).WithTenant(tenant));
    }
}
