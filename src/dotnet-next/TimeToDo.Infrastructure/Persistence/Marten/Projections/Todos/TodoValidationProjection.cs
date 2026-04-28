using Marten.Events.Aggregation;
using TimeToDo.Domain.Todos.Events;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

public class TodoValidationProjection : SingleStreamProjection<TodoValidationData, Guid>
{
    public TodoValidationData Create(TodoCreatedEvent @event)
    {
        return new TodoValidationData
        {
            Id = @event.Id,
            ParentTodoId = @event.ParentTodoId,
            ChildTodoIds = new List<Guid>(),
            CustomerId = @event.CustomerId,
            IsDeleted = false,
            IsArchived = false
        };
    }

    public TodoValidationData Create(TodoMigratedEvent @event)
    {
        return new TodoValidationData
        {
            Id = @event.Id,
            ParentTodoId = @event.ParentTodoId,
            ChildTodoIds = @event.ChildTodoIds,
            CustomerId = @event.CustomerId,
            IsDeleted = false,
            IsArchived = @event.IsArchived
        };
    }

    public TodoValidationData Apply(TodoUpdatedEvent @event, TodoValidationData current)
    {
        return current with
        {
            CustomerId = @event.CustomerId.HasValue ? @event.CustomerId.Value : current.CustomerId
        };
    }

    public TodoValidationData Apply(TodoDeletedEvent @event, TodoValidationData current)
    {
        return current with { IsDeleted = true };
    }

    public TodoValidationData Apply(TodoArchivedEvent @event, TodoValidationData current)
    {
        return current with { IsArchived = @event.IsArchived };
    }

    public TodoValidationData Apply(TodoChildAddedEvent @event, TodoValidationData current)
    {
        var children = new List<Guid>(current.ChildTodoIds);
        if (!children.Contains(@event.ChildId))
            children.Add(@event.ChildId);

        return current with { ChildTodoIds = children };
    }

    public TodoValidationData Apply(TodoChildRemovedEvent @event, TodoValidationData current)
    {
        var children = new List<Guid>(current.ChildTodoIds);
        children.Remove(@event.ChildId);

        return current with { ChildTodoIds = children };
    }

    public TodoValidationData Apply(TodoParentChangedEvent @event, TodoValidationData current)
    {
        return current with
        {
            ParentTodoId = @event.NewParentId,
            CustomerId = @event.InheritedCustomerId ?? current.CustomerId
        };
    }
}
