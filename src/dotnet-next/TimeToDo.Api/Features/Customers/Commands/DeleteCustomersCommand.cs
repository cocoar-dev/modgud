using ErrorOr;
using Marten;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

namespace TimeToDo.Api.Features.Customers.Commands;

public record DeleteCustomersCommand(List<Guid> CustomerIds);

public class DeleteCustomersHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteCustomersCommand command,
        CancellationToken ct)
    {
        foreach (var id in command.CustomerIds)
        {
            var customer = await session.LoadAsync<CustomerView>(id, ct);
            if (customer is null || customer.IsDeleted)
                continue;

            session.Events.Append(id, new CustomerDeletedEvent(id));
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
