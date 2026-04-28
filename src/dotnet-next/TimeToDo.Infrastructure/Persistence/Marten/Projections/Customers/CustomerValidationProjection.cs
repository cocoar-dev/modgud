using Marten.Events.Aggregation;
using TimeToDo.Domain.Customers.Events;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

public class CustomerValidationProjection : SingleStreamProjection<CustomerValidationData, Guid>
{
    public CustomerValidationData Create(CustomerCreatedEvent @event)
    {
        return new CustomerValidationData
        {
            Id = @event.Id,
            Name = @event.Name.OrDefault()
        };
    }

    public CustomerValidationData Create(CustomerMigratedEvent @event)
    {
        return new CustomerValidationData
        {
            Id = @event.Id,
            Name = @event.Name.OrDefault()
        };
    }

    public CustomerValidationData Apply(CustomerUpdatedEvent @event, CustomerValidationData current)
    {
        return current with
        {
            Name = @event.Name.HasValue ? @event.Name.Value : current.Name
        };
    }

    public CustomerValidationData Apply(CustomerDeletedEvent @event, CustomerValidationData current)
    {
        return current with { IsDeleted = true };
    }
}
