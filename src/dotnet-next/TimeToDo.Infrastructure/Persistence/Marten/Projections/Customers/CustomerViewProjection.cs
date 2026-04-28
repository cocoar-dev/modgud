using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Infrastructure.Events;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

public class CustomerViewProjection : SingleStreamProjection<CustomerView, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<CustomerView> slice)
    {
        var snapshot = slice.Snapshot;
        if (snapshot is null || !ProjectionSideEffects.Enabled)
            return ValueTask.CompletedTask;

        var action = slice.Events().Any(e => e.Data is CustomerDeletedEvent)
            ? SignalRDispatchAction.Deleted
            : slice.Events().Any(e => e.Data is CustomerCreatedEvent or CustomerMigratedEvent)
                ? SignalRDispatchAction.Created
                : SignalRDispatchAction.Updated;

        slice.PublishMessage(new CustomerViewSignalRDispatch(action, snapshot, snapshot.Id));

        return ValueTask.CompletedTask;
    }

    public CustomerView Create(CustomerCreatedEvent @event)
    {
        return new CustomerView
        {
            Id = @event.Id,
            Name = @event.Name.OrDefault(),
            IsImportant = @event.IsImportant.OrDefault(),
            IsArchived = false
        };
    }

    public CustomerView Create(CustomerMigratedEvent @event)
    {
        return new CustomerView
        {
            Id = @event.Id,
            Name = @event.Name.OrDefault(),
            IsImportant = @event.IsImportant.OrDefault(),
            IsArchived = @event.IsArchived.OrDefault(),
        };
    }

    public CustomerView Apply(CustomerUpdatedEvent @event, CustomerView current)
    {
        return current with
        {
            Name = @event.Name.HasValue ? @event.Name.Value : current.Name,
            IsImportant = @event.IsImportant.HasValue ? @event.IsImportant.Value : current.IsImportant,
            IsArchived = @event.IsArchived.HasValue ? @event.IsArchived.Value : current.IsArchived
        };
    }

    public CustomerView Apply(CustomerDeletedEvent @event, CustomerView current)
    {
        return current with { IsDeleted = true };
    }
}
