using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.Contracts;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;

namespace TimeToDo.Infrastructure.QueryServices;

public class MartenCustomerQueryService(IDocumentSession session) : ICustomerQueryService
{
    public async Task<IReadOnlyList<CustomerDto>> GetAllAsync(CancellationToken ct = default)
    {
        var customers = await session.Query<CustomerDocument>()
            .Where(c => !c.IsArchived)
            .ToListAsync(ct);

        return customers.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<CustomerDto>> GetArchivedAsync(CancellationToken ct = default)
    {
        var customers = await session.Query<CustomerDocument>()
            .Where(c => c.IsArchived)
            .ToListAsync(ct);

        return customers.Select(ToDto).ToList();
    }

    public async Task<CustomerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var customer = await session.LoadAsync<CustomerDocument>(id, ct);
        return customer != null ? ToDto(customer) : null;
    }

    private static CustomerDto ToDto(CustomerDocument document)
    {
        return new CustomerDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Name = document.Name,
            Important = document.IsImportant,
            IsArchived = document.IsArchived
        };
    }
}
