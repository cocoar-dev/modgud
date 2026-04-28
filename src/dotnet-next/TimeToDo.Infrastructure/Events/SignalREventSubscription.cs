using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Infrastructure.Events;

public class SignalREventSubscription(DataEventDispatcher eventDispatcher, IServiceProvider serviceProvider) : ISubscription
{
    private enum ActionType { Created, Updated, Deleted }

    private record DispatchEntry(Guid StreamId, string Subject, ActionType Action);

    public async Task<IChangeListener> ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var store = serviceProvider.GetRequiredService<IDocumentStore>();

        // Collect dispatch entries, dedup per stream (Deleted wins)
        var entries = new Dictionary<(Guid StreamId, string Subject), DispatchEntry>();

        foreach (var @event in page.Events)
        {
            var entry = MapEvent(@event);
            if (entry == null)
                continue;

            var key = (entry.StreamId, entry.Subject);

            if (entries.TryGetValue(key, out var existing))
            {
                // Deleted wins over other actions
                if (entry.Action == ActionType.Deleted)
                    entries[key] = entry;
            }
            else
            {
                entries[key] = entry;
            }
        }

        // Dispatch all entries
        await using var querySession = store.QuerySession();

        foreach (var entry in entries.Values)
        {
            switch (entry.Action)
            {
                case ActionType.Deleted:
                    eventDispatcher.DispatchDeletedEvent(entry.Subject, new ShortGuid(entry.StreamId).ToString());
                    break;

                case ActionType.Created:
                case ActionType.Updated:
                    var view = await LoadView(querySession, entry.StreamId, entry.Subject, cancellationToken);
                    if (view == null)
                        continue;

                    if (entry.Action == ActionType.Created)
                        eventDispatcher.DispatchCreatedEvent(entry.Subject, view);
                    else
                        eventDispatcher.DispatchUpdatedEvent(entry.Subject, view);
                    break;
            }
        }

        return NullChangeListener.Instance;
    }

    private static DispatchEntry? MapEvent(IEvent @event)
    {
        return @event.Data switch
        {
            // User events
            UserCreatedEvent e => new DispatchEntry(e.Id, "User", ActionType.Created),
            UserMigratedEvent e => new DispatchEntry(e.Id, "User", ActionType.Created),
            UserUpdatedEvent e => new DispatchEntry(e.Id, "User", ActionType.Updated),
            UserDeletedEvent e => new DispatchEntry(e.Id, "User", ActionType.Deleted),

            // Customer events
            CustomerCreatedEvent e => new DispatchEntry(e.Id, "Customer", ActionType.Created),
            CustomerMigratedEvent e => new DispatchEntry(e.Id, "Customer", ActionType.Created),
            CustomerUpdatedEvent e => new DispatchEntry(e.Id, "Customer", ActionType.Updated),
            CustomerDeletedEvent e => new DispatchEntry(e.Id, "Customer", ActionType.Deleted),

            _ => null
        };
    }

    private static async Task<object?> LoadView(IQuerySession session, Guid id, string subject, CancellationToken ct)
    {
        return subject switch
        {
            "User" => await session.LoadAsync<UserView>(id, ct),
            "Customer" => await session.LoadAsync<CustomerView>(id, ct),
            _ => null
        };
    }
}
