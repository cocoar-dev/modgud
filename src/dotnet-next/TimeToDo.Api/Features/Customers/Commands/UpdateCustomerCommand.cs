using ErrorOr;
using Marten;
using TimeToDo.Domain.Common;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Application.DTOs.Customer;

namespace TimeToDo.Api.Features.Customers.Commands;

public record UpdateCustomerCommand(Guid CustomerId, Optional<string> Name, Optional<bool> Important, Optional<bool> IsArchived);

public class UpdateCustomerHandler(IDocumentSession session)
{
    public async Task<ErrorOr<CustomerListDto>> Handle(
        UpdateCustomerCommand command,
        CancellationToken ct)
    {
        var customer = await session.LoadAsync<CustomerView>(command.CustomerId, ct);
        if (customer is null || customer.IsDeleted)
            return Error.NotFound("Customer.NotFound", "Customer not found");

        if (command.Name.HasValue)
        {
            if (string.IsNullOrWhiteSpace(command.Name.Value))
                return Error.Validation("Name.Required", "Name is required");

            var existing = await session.Query<CustomerValidationData>()
                .FirstOrDefaultAsync(c => c.Name == command.Name.Value && c.Id != command.CustomerId && !c.IsDeleted, ct);
            if (existing != null)
                return Error.Conflict("Name.Duplicate", $"Customer with name '{command.Name.Value}' already exists");
        }

        var @event = new CustomerUpdatedEvent(
            command.CustomerId,
            Name: command.Name,
            IsImportant: command.Important,
            IsArchived: command.IsArchived
        );

        session.Events.Append(command.CustomerId, @event);

        // Label sync for TodoViews is handled by ReferenceSyncHandlers
        // via Marten Event Forwarding (CustomerUpdatedEvent → Wolverine → sync handlers)

        await session.SaveChangesAsync(ct);

        var updatedView = customer with
        {
            Name = command.Name.HasValue ? command.Name.Value : customer.Name,
            IsImportant = command.Important.HasValue ? command.Important.Value : customer.IsImportant,
            IsArchived = command.IsArchived.HasValue ? command.IsArchived.Value : customer.IsArchived
        };
        return updatedView.ToListDto();
    }
}
