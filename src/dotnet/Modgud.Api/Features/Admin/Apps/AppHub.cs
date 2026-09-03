using System.Reactive.Linq;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using Modgud.Api.Realtime;

namespace Modgud.Api.Features.Admin.Apps;

/// <summary>
/// Per-entity SignalR pipe for the admin Applications grid. The endpoint layer
/// (admin CRUD in <see cref="AppsEndpoints"/>) pushes Created/Updated/Deleted
/// notifications through <see cref="DataEventDispatcher"/> with subject "App";
/// this hub forwards them to the connected admin SPA clients so an app changed
/// out-of-band — another admin, another tab — shows up live without a manual
/// reload. (A draft APPLY bypasses the endpoint layer by design; the SPA
/// re-syncs every entity store after a successful apply instead.)
/// </summary>
[MessageName("AppActions")]
public class AppHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        // Scope to this connection's realm (resolved at connect by
        // RealmMiddleware). Untagged events never match → no cross-realm leak.
        var http = Context.GetHttpContext();
        var realm = HubAuthorization.CallerRealm(http);

        var source = eventDispatcher.Notifications
            .Where(ev => ev.Subject == "App" && ev.Tenant == realm);

        // Per-method permission gate: match the REST list endpoint's
        // app:read instead of relying on UIHub's bare [Authorize].
        return HubAuthorization.AuthorizedRealmStream(http, realm, "app:read", source);
    }
}
