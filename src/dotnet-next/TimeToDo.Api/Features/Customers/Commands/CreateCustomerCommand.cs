using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Application.DTOs.Customer;

namespace TimeToDo.Api.Features.Customers.Commands;

public record CreateCustomerCommand(string? Name, bool Important);

public class CreateCustomerHandler(IDocumentSession session)
{
    public async Task<ErrorOr<CustomerListDto>> Handle(
        CreateCustomerCommand command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Error.Validation("Name.Required", "Name is required");

        var existing = await session.Query<CustomerValidationData>()
            .FirstOrDefaultAsync(c => c.Name == command.Name && !c.IsDeleted, ct);
        if (existing != null)
            return Error.Conflict("Name.Duplicate", $"Customer with name '{command.Name}' already exists");

        var id = Guid.NewGuid();
        var @event = new CustomerCreatedEvent(id, command.Name, command.Important);

        session.Events.StartStream<CustomerView>(id, @event);
        await session.SaveChangesAsync(ct);

        return new CustomerListDto
        {
            Id = new ShortGuid(id).ToString(),
            Name = command.Name,
            Important = command.Important,
            IsArchived = false
        };
    }
}
