using ErrorOr;
using Marten;
using TimeToDo.Domain.Common;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

namespace TimeToDo.Api.Features.Customers.Commands;

public record ArchiveCustomersCommand(List<Guid> CustomerIds, bool Restore);

public class ArchiveCustomersHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(
        ArchiveCustomersCommand command,
        CancellationToken ct)
    {
        foreach (var id in command.CustomerIds)
        {
            var customer = await session.LoadAsync<CustomerView>(id, ct);
            if (customer is null || customer.IsDeleted)
                continue;

            var @event = new CustomerUpdatedEvent(
                id,
                Name: Optional<string>.None,
                IsImportant: Optional<bool>.None,
                IsArchived: !command.Restore
            );

            session.Events.Append(id, @event);
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
