using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using BuildingBlocks.EventDispatcher;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

namespace TimeToDo.Api.Features.Customers;

[MessageName("CustomerActions")]
public class CustomerHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        return eventDispatcher.Notifications
            .Where(ev => ev.Subject == "Customer")
            .Select(de =>
            {
                var newPayload = new List<object>();
                foreach (var p in de.Payload)
                {
                    if (p is CustomerView v)
                        newPayload.Add(v.ToListDto());
                    else
                        newPayload.Add(p);
                }
                return new DataEvent(de.Action, de.Subject, newPayload);
            });
    }
}
