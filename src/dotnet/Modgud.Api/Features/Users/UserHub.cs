using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.SignalR;
using BuildingBlocks.EventDispatcher;
using Modgud.Api.Realtime;
using Modgud.Authentication.ExtensionMethods;
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

    /// <summary>
    /// B4-AP-06 — the caller's own user events, for the profile page: a user without
    /// <c>user:read</c> still sees an approval or an admin edit of their profile live.
    /// Needs no permission beyond being signed in, because it only ever carries the
    /// caller's own record.
    /// </summary>
    public IObservable<DataEvent> SubscribeSelf()
    {
        var http = Context.GetHttpContext();
        return SelfEvents(eventDispatcher.Notifications, HubAuthorization.CallerRealm(http), http?.GetUserId());
    }

    /// <summary>
    /// User events of <paramref name="realm"/> reduced to the record of <paramref name="userId"/>;
    /// an event that does not touch it is dropped. No realm or no caller: nothing passes.
    /// </summary>
    internal static IObservable<DataEvent> SelfEvents(IObservable<DataEvent> notifications, string? realm, Guid? userId)
    {
        if (string.IsNullOrEmpty(realm) || userId is not { } me)
            return Observable.Empty<DataEvent>();

        return notifications
            .Where(ev => ev.Subject == "User" && ev.Tenant == realm)
            .Select(ev => (ev, own: ev.Payload.OfType<UserView>().Where(v => v.Id == me).ToList()))
            .Where(x => x.own.Count > 0)
            .Select(x => new DataEvent(x.ev.Action, x.ev.Subject, x.own.Select(v => (object)v.ToDto()).ToList()));
    }
}
