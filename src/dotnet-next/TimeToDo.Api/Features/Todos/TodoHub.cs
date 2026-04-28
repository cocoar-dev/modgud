using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using BuildingBlocks.EventDispatcher;
using Marten;
using Microsoft.AspNetCore.SignalR;
using TimeToDo.Authentication.ExtensionMethods;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos;

[MessageName("TodoActions")]
public class TodoHub(DataEventDispatcher eventDispatcher, IDocumentStore store)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        var httpContext = Context.GetHttpContext()!;
        var userId = httpContext.GetUserId();

        return eventDispatcher.Notifications
            .Where(ev => ev.Subject == "Todo")
            .Select(de => Observable.FromAsync(async () =>
            {
                await using var session = store.LightweightSession();
                var newPayload = new List<object>();
                foreach (var p in de.Payload)
                {
                    if (p is TodoView view)
                    {
                        var dto = await view.ToDtoEnrichedAsync(session, userId);
                        newPayload.Add(dto);
                    }
                    else
                    {
                        newPayload.Add(p);
                    }
                }
                return new DataEvent(de.Action, de.Subject, newPayload);
            }))
            .Concat();
    }
}
