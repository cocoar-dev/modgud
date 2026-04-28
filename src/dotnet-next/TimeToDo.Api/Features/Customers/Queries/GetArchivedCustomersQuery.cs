using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Application.DTOs.Customer;

namespace TimeToDo.Api.Features.Customers.Queries;

public record GetArchivedCustomersQuery(Guid? UserId = null);

public class GetArchivedCustomersHandler(IDocumentSession session, IAccessPolicyEngine accessPolicyEngine)
{
    public async Task<ErrorOr<List<CustomerListDto>>> Handle(
        GetArchivedCustomersQuery query,
        CancellationToken ct)
    {
        var queryable = session.Query<CustomerView>().Where(c => c.IsArchived && !c.IsDeleted);
        if (query.UserId.HasValue)
        {
            var accessFilter = await accessPolicyEngine.BuildCustomerFilterForActionAsync(
                query.UserId.Value, "customer:read", ct);
            if (accessFilter is not null)
                queryable = queryable.Where(accessFilter);
        }

        var customers = await queryable.OrderBy(c => c.Name).ToListAsync(ct);

        return customers.Select(c => c.ToListDto()).ToList();
    }
}
