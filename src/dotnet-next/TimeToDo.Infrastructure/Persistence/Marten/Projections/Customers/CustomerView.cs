using Marten.Schema;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

[DocumentAlias("customer_view")]
public record CustomerView
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public bool IsImportant { get; init; }
    public bool IsArchived { get; init; }
    public bool IsDeleted { get; init; }
}
