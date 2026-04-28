using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Application.DTOs.Customer;

namespace TimeToDo.Api.Features.Customers.Queries;

public record GetCustomerByIdQuery(Guid CustomerId, Guid? UserId = null);

public class GetCustomerByIdHandler(IDocumentSession session, IAccessPolicyEngine accessPolicyEngine)
{
    public async Task<ErrorOr<CustomerDto>> Handle(
        GetCustomerByIdQuery query,
        CancellationToken ct)
    {
        var queryable = session.Query<CustomerView>().Where(c => !c.IsDeleted);
        if (query.UserId.HasValue)
        {
            var accessFilter = await accessPolicyEngine.BuildCustomerFilterForActionAsync(
                query.UserId.Value, "customer:read", ct);
            if (accessFilter is not null)
                queryable = queryable.Where(accessFilter);
        }

        var customer = await queryable.FirstOrDefaultAsync(c => c.Id == query.CustomerId, ct);
        if (customer is null)
            return Error.NotFound("Customer.NotFound", "Customer not found");

        return customer.ToDto();
    }
}
