using Marten.Schema;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

[DocumentAlias("todo_validation")]
public record TodoValidationData
{
    public Guid Id { get; init; }
    public Guid? ParentTodoId { get; init; }
    public List<Guid> ChildTodoIds { get; init; } = new();
    public Guid? CustomerId { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsArchived { get; init; }
}
