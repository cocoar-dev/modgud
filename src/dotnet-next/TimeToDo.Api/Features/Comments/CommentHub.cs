using System.Reactive.Linq;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using BuildingBlocks.EventDispatcher;
using Marten;
using Microsoft.AspNetCore.SignalR;
using TimeToDo.Authentication.ExtensionMethods;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;

namespace TimeToDo.Api.Features.Comments;

[MessageName("CommentActions")]
public class CommentHub(DataEventDispatcher eventDispatcher, IDocumentStore store) : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
    {
        var httpContext = Context.GetHttpContext()!;
        var userId = httpContext.GetUserId();

        return eventDispatcher.Notifications
            .Where(ev => ev.Subject == "Comment")
            .Select(de => Observable.FromAsync(async () =>
            {
                await using var session = store.LightweightSession();
                var newPayload = new List<object>();
                foreach (var p in de.Payload)
                {
                    if (p is CommentView view)
                    {
                        var dto = await view.ToListDtoEnrichedAsync(session, userId);
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
