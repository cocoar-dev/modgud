using System.Reactive.Linq;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using Modgud.Api.Realtime;

namespace Modgud.Api.Features.InviteCodes;

/// <summary>
/// ADR-0012 — per-entity SignalR pipe for the admin Invite-Codes grid. The
/// endpoint layer pushes Created/Deleted notifications through
/// <see cref="DataEventDispatcher"/> with subject "InviteCode" (payload carries
/// the AppId); this hub forwards them, realm-scoped, to the connected admin SPA
/// clients, which reload the list when the event's AppId matches the app they
/// are showing. Mirrors <c>ServiceAccountHub</c>.
/// </summary>
[MessageName("InviteCodeActions")]
public class InviteCodeHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        var http = Context.GetHttpContext();
        var realm = HubAuthorization.CallerRealm(http);

        var source = eventDispatcher.Notifications
            .Where(ev => ev.Subject == "InviteCode" && ev.Tenant == realm);

        // Gate on the same permission as the REST list endpoint's admin path.
        return HubAuthorization.AuthorizedRealmStream(http, realm, "invite-code:read", source);
    }
}
