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
}
