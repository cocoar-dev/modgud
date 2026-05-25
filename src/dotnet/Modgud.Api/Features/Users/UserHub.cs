using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using BuildingBlocks.EventDispatcher;
using Modgud.Infrastructure.Persistence.Marten.Mappers;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Features.Users;

[MessageName("UserActions")]
public class UserHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        return eventDispatcher.Notifications
            .Where(ev => ev.Subject == "User")
            .Select(de =>
            {
                var newPayload = new List<object>();
                foreach (var p in de.Payload)
                {
                    if (p is UserView v)
                        newPayload.Add(v.ToDto());
                    else
                        newPayload.Add(p);
                }
                return new DataEvent(de.Action, de.Subject, newPayload);
            });
    }
}
