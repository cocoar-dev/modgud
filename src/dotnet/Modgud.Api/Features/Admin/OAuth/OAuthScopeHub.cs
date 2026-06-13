using System.Reactive.Linq;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using Modgud.Api.Realtime;

namespace Modgud.Api.Features.Admin.OAuth;

/// <summary>
/// Per-entity SignalR pipe for the admin OAuth-scopes grid. The endpoint layer
/// (admin CRUD in <see cref="OAuthScopesEndpoints"/>) pushes Created/Updated/
/// Deleted notifications through <see cref="DataEventDispatcher"/> with subject
/// "OAuthScope"; this hub forwards them to the connected admin SPA clients so a
/// scope created out-of-band — implicitly when an OAuth API is added, another
/// admin, another tab — shows up live without a manual reload.
/// </summary>
[MessageName("OAuthScopeActions")]
public class OAuthScopeHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        // Scope to this connection's realm (resolved at connect by
        // RealmMiddleware). Untagged events never match → no cross-realm leak.
        var http = Context.GetHttpContext();
        var realm = HubAuthorization.CallerRealm(http);

        var source = eventDispatcher.Notifications
            .Where(ev => ev.Subject == "OAuthScope" && ev.Tenant == realm);

        // Per-method permission gate: match the REST list endpoint's
        // oauth-scope:read instead of relying on UIHub's bare [Authorize].
        return HubAuthorization.AuthorizedRealmStream(http, realm, "oauth-scope:read", source);
    }
}
