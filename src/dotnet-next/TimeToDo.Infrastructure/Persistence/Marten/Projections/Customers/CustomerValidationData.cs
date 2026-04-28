using Marten.Schema;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

[DocumentAlias("customer_validation")]
public record CustomerValidationData
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public bool IsDeleted { get; init; }
}
