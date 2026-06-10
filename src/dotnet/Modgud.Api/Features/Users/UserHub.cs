using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using BuildingBlocks.EventDispatcher;
using Modgud.Api.Realtime;
using Modgud.Infrastructure.Persistence.Marten.Mappers;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Features.Users;

[MessageName("UserActions")]
public class UserHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        // Realm of THIS connection, resolved server-side by RealmMiddleware at
        // connect (Host → tenant). Scope the stream to it so a connection only
        // ever sees its own realm's events. Untagged events (Tenant == null)
        // never match a real realm → dropped (fail-closed, no cross-realm leak).
        var http = Context.GetHttpContext();
        var realm = HubAuthorization.CallerRealm(http);

        var source = eventDispatcher.Notifications
            .Where(ev => ev.Subject == "User" && ev.Tenant == realm)
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

        // Per-method permission gate (audit H2): this stream carries user PII
        // (email, names). Require user:read — the same gate as the REST list
        // endpoint — instead of relying on UIHub's bare [Authorize].
        return HubAuthorization.AuthorizedRealmStream(http, realm, "user:read", source);
    }
}
