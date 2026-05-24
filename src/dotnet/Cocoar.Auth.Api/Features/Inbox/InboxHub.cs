using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using BuildingBlocks.EventDispatcher;
using Microsoft.AspNetCore.SignalR;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Mappers;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Cocoar.Auth.Api.Features.Inbox;

/// <summary>
/// Per-user SignalR push for inbox items. Every connected client subscribes
/// to the "InboxItem" subject, but the per-event filter here keeps each
/// client's stream limited to items addressed to the current user.
/// </summary>
[MessageName("InboxActions")]
public class InboxHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        var httpContext = Context.GetHttpContext()!;
        var userId = httpContext.GetUserId();

        // No userId → empty stream. The hub already requires authentication
        // via UIHub's [Authorize], so this is defensive only.
        if (userId is null)
            return Observable.Empty<DataEvent>();

        return eventDispatcher.Notifications
            .Where(ev => ev.Subject == "InboxItem")
            .Select(de =>
            {
                var newPayload = new List<object>();
                foreach (var p in de.Payload)
                {
                    // Per-event recipient filter — subject-level subscribe
                    // gives us every inbox event, so the per-user gate has
                    // to live here.
                    if (p is InboxItemView view && view.RecipientUserId == userId.Value)
                    {
                        newPayload.Add(view.ToDto());
                    }
                    else if (p is string)
                    {
                        // We don't currently dispatch Delete events for inbox
                        // (Dismiss is the user-facing equivalent), but keep
                        // the shape consistent with the other hubs.
                        newPayload.Add(p);
                    }
                }
                return newPayload.Count > 0
                    ? new DataEvent(de.Action, de.Subject, newPayload)
                    : null!;
            })
            .Where(e => e is not null);
    }
}
