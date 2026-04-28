using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Application.DTOs.Customer;

namespace TimeToDo.Api.Features.Customers.Queries;

public record GetAllCustomersQuery(Guid? UserId = null, bool IncludeArchived = true, int? Skip = null, int? Take = null);

public class GetAllCustomersHandler(IDocumentSession session, IAccessPolicyEngine accessPolicyEngine)
{
    public async Task<ErrorOr<List<CustomerListDto>>> Handle(
        GetAllCustomersQuery query,
        CancellationToken ct)
    {
        var queryable = query.IncludeArchived
            ? session.Query<CustomerView>().Where(c => !c.IsDeleted)
            : session.Query<CustomerView>().Where(c => !c.IsArchived && !c.IsDeleted);

        // Apply access policy filter
        if (query.UserId.HasValue)
        {
            var accessFilter = await accessPolicyEngine.BuildCustomerFilterForActionAsync(query.UserId.Value, "customer:read", ct);
            if (accessFilter is not null)
                queryable = queryable.Where(accessFilter);
        }

        IEnumerable<CustomerView> customers = await queryable
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        if (query.Skip.HasValue)
            customers = customers.Skip(query.Skip.Value);
        if (query.Take.HasValue)
            customers = customers.Take(query.Take.Value);

        return customers.Select(c => c.ToListDto()).ToList();
    }
}
