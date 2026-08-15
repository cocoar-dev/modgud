using System.Reactive.Linq;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using Modgud.Api.Realtime;

namespace Modgud.Api.Features.Positions;

/// <summary>
/// Per-entity SignalR pipe for the admin Positions grid — the counterpart of
/// <c>ServiceAccountHub</c>. The endpoint layer pushes Created/Updated/Deleted
/// through <see cref="DataEventDispatcher"/> with subject "Position"; this hub
/// forwards them to the connected admin SPA clients.
///
/// Missing since MG-FT-01: the Pinia store has always subscribed with
/// <c>enableSignalR: true</c>, so every admin session logged
/// "Method 'PositionActions.Subscribe' not found!" and the grid never updated
/// live.
/// </summary>
[MessageName("PositionActions")]
public class PositionHub(DataEventDispatcher eventDispatcher, AppSettings settings)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        // Defense in depth like the REST surface: while the feature is dark
        // there is nothing to stream (and nothing dispatches "Position" either).
        if (!settings.Features.PositionTerminals) return Observable.Empty<DataEvent>();

        // Scope to this connection's realm (resolved at connect by
        // RealmMiddleware). Untagged events never match → no cross-realm leak.
        var http = Context.GetHttpContext();
        var realm = HubAuthorization.CallerRealm(http);

        var source = eventDispatcher.Notifications
            .Where(ev => ev.Subject == "Position" && ev.Tenant == realm);

        // Per-method permission gate, matching the REST list endpoint.
        return HubAuthorization.AuthorizedRealmStream(http, realm, "position:read", source);
    }
}
