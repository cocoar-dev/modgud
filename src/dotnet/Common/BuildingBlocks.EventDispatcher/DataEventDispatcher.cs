using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace BuildingBlocks.EventDispatcher;

public class DataEventDispatcher
{
    private Subject<DataEvent> NotificationsSubject { get; } = new();
    public IObservable<DataEvent> Notifications => NotificationsSubject.AsObservable();

    public void DispatchEvent(DataEvent @event)
    {
        NotificationsSubject.OnNext(@event);
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
