using System.Reactive.Linq;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using Modgud.Api.Realtime;

namespace Modgud.Api.Features.ServiceAccounts;

/// <summary>
/// Per-entity SignalR pipe for the admin Service-Accounts grid. The endpoint
/// layer pushes Created/Updated/Deleted notifications through
/// <see cref="DataEventDispatcher"/> with subject "ServiceAccount"; this hub
/// just forwards them to the connected admin SPA clients.
/// </summary>
[MessageName("ServiceAccountActions")]
public class ServiceAccountHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        // Scope to this connection's realm (resolved at connect by
        // RealmMiddleware). Untagged events never match → no cross-realm leak.
        var http = Context.GetHttpContext();
        var realm = HubAuthorization.CallerRealm(http);

        var source = eventDispatcher.Notifications
            .Where(ev => ev.Subject == "ServiceAccount" && ev.Tenant == realm);

        // Per-method permission gate (audit H2): match the REST list endpoint's
        // service-account:read instead of relying on UIHub's bare [Authorize].
        return HubAuthorization.AuthorizedRealmStream(http, realm, "service-account:read", source);
    }
}
