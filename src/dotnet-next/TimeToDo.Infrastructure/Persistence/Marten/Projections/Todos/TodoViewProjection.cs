using TimeToDo.Authorization.Principals;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Infrastructure.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

public class TodoViewProjection : SingleStreamProjection<TodoView, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<TodoView> slice)
    {
        var snapshot = slice.Snapshot;
        if (snapshot is null || !ProjectionSideEffects.Enabled)
            return ValueTask.CompletedTask;

        var action = DetermineDispatchAction(slice);

        // Publish a Wolverine message that will be dispatched only after this projection batch commits.
        slice.PublishMessage(new TodoViewSignalRDispatch(action, snapshot, snapshot.Id));

        return ValueTask.CompletedTask;
    }

    private static SignalRDispatchAction DetermineDispatchAction(IEventSlice<TodoView> slice)
    {
        if (slice.Events().Any(e => e.Data is TodoDeletedEvent)) return SignalRDispatchAction.Deleted;
        if (slice.Events().Any(e => e.Data is TodoCreatedEvent or TodoMigratedEvent)) return SignalRDispatchAction.Created;
        return SignalRDispatchAction.Updated;
    }

    public async Task<TodoView> Create(TodoCreatedEvent @event, IQuerySession session)
    {
        var customer = @event.CustomerId.HasValue
            ? new ViewRef { Id = @event.CustomerId.Value, Label = (await session.LoadAsync<CustomerView>(@event.CustomerId.Value))?.Name }
            : null;

        var users = await LoadUsersAsync(session, [@event.CreatedById, ..@event.ResponsibleUserIds]);

        return new TodoView
        {
            Id = @event.Id,
            Title = @event.Title,
            Description = @event.Description,
            DueDate = @event.DueDate,
            Status = @event.Status,
            Customer = customer,
            Responsibles = @event.ResponsibleUserIds
                .Select(uid => BuildPrincipalRef(users, uid))
                .ToList(),
            ParentTodoId = @event.ParentTodoId,
            ChildTodoIds = new List<Guid>(),
            IsArchived = false,
            IsCritical = @event.IsCritical,
            IsAwaitingFeedback = @event.IsAwaitingFeedback,
            CommentsCount = 0,
            CreatedAt = @event.CreatedAt,
            CreatedBy = BuildPrincipalRef(users, @event.CreatedById),
            IsDeleted = false
        };
    }

    public async Task<TodoView> Create(TodoMigratedEvent @event, IQuerySession session)
    {
        var customer = @event.CustomerId.HasValue
            ? new ViewRef { Id = @event.CustomerId.Value, Label = (await session.LoadAsync<CustomerView>(@event.CustomerId.Value))?.Name }
            : null;

        var allUserIds = new List<Guid>(@event.ResponsibleUserIds) { @event.CreatedById };
        if (@event.UpdatedById.HasValue) allUserIds.Add(@event.UpdatedById.Value);
        var users = await LoadUsersAsync(session, allUserIds);

        return new TodoView
        {
            Id = @event.Id,
            Title = @event.Title,
            Description = @event.Description,
            DueDate = @event.DueDate,
            Status = @event.Status,
            Customer = customer,
            Responsibles = @event.ResponsibleUserIds
                .Select(uid => BuildPrincipalRef(users, uid))
                .ToList(),
            ParentTodoId = @event.ParentTodoId,
            ChildTodoIds = @event.ChildTodoIds,
            IsArchived = @event.IsArchived,
            IsCritical = @event.IsCritical,
            IsAwaitingFeedback = @event.IsAwaitingFeedback,
            CommentsCount = @event.CommentsCount,
            CreatedAt = @event.CreatedAt,
            CreatedBy = BuildPrincipalRef(users, @event.CreatedById),
            UpdatedAt = @event.UpdatedAt,
            UpdatedBy = @event.UpdatedById.HasValue
                ? BuildPrincipalRef(users, @event.UpdatedById.Value)
                : null,
            IsDeleted = false
        };
    }

    public async Task<TodoView> Apply(TodoUpdatedEvent @event, TodoView current, IQuerySession session)
    {
        // Resolve customer
        var customer = current.Customer;
        if (@event.CustomerId.HasValue)
        {
            var newCustomerId = @event.CustomerId.Value;
            customer = newCustomerId.HasValue
                ? new ViewRef { Id = newCustomerId.Value, Label = (await session.LoadAsync<CustomerView>(newCustomerId.Value))?.Name }
                : null;
        }

        // Resolve responsibles + updatedBy
        var userIdsToLoad = new List<Guid> { @event.UpdatedById };
        if (@event.ResponsibleUserIds.HasValue && @event.ResponsibleUserIds.Value is { } responsibleIds)
            userIdsToLoad.AddRange(responsibleIds);
        var users = await LoadUsersAsync(session, userIdsToLoad);

        var responsibles = current.Responsibles;
        if (@event.ResponsibleUserIds.HasValue && @event.ResponsibleUserIds.Value is { } responsibleViews)
        {
            responsibles = responsibleViews
                .Select(uid => BuildPrincipalRef(users, uid))
                .ToList();
        }

        return current with
        {
            Title = @event.Title.HasValue && @event.Title.Value is { } title ? title : current.Title,
            Description = @event.Description.HasValue ? @event.Description.Value : current.Description,
            DueDate = @event.DueDate.HasValue ? @event.DueDate.Value : current.DueDate,
            Status = @event.Status.HasValue ? @event.Status.Value : current.Status,
            Customer = customer,
            Responsibles = responsibles,
            IsCritical = @event.IsCritical.HasValue ? @event.IsCritical.Value : current.IsCritical,
            IsAwaitingFeedback = @event.IsAwaitingFeedback.HasValue ? @event.IsAwaitingFeedback.Value : current.IsAwaitingFeedback,
            UpdatedAt = @event.UpdatedAt,
            UpdatedBy = BuildPrincipalRef(users, @event.UpdatedById)
        };
    }

    public TodoView Apply(TodoDeletedEvent @event, TodoView current)
    {
        return current with { IsDeleted = true };
    }

    public TodoView Apply(TodoStatusChangedEvent @event, TodoView current)
    {
        return current with { Status = @event.Status };
    }

    public TodoView Apply(TodoFlagsChangedEvent @event, TodoView current)
    {
        return current with
        {
            IsCritical = @event.IsCritical.HasValue ? @event.IsCritical.Value : current.IsCritical,
            IsAwaitingFeedback = @event.IsAwaitingFeedback.HasValue ? @event.IsAwaitingFeedback.Value : current.IsAwaitingFeedback,
        };
    }

    public TodoView Apply(TodoArchivedEvent @event, TodoView current)
    {
        return current with { IsArchived = @event.IsArchived };
    }

    public TodoView Apply(TodoChildAddedEvent @event, TodoView current)
    {
        var children = new List<Guid>(current.ChildTodoIds) { @event.ChildId };
        return current with { ChildTodoIds = children };
    }

    public TodoView Apply(TodoChildRemovedEvent @event, TodoView current)
    {
        var children = current.ChildTodoIds.Where(id => id != @event.ChildId).ToList();
        return current with { ChildTodoIds = children };
    }

    public async Task<TodoView> Apply(TodoParentChangedEvent @event, TodoView current, IQuerySession session)
    {
        var customer = current.Customer;
        if (@event.InheritedCustomerId.HasValue)
        {
            customer = new ViewRef
            {
                Id = @event.InheritedCustomerId.Value,
                Label = (await session.LoadAsync<CustomerView>(@event.InheritedCustomerId.Value))?.Name
            };
        }

        return current with { ParentTodoId = @event.NewParentId, Customer = customer };
    }

    public TodoView Apply(TodoCommentsCountChangedEvent @event, TodoView current)
    {
        return current with { CommentsCount = @event.CommentsCount };
    }

    // ── Principal label + ref resolution ───────────────────────────

    /// <summary>
    /// Load principals from the polymorphic Principal document (inline projection,
    /// always consistent). NOT from UserView (async projection, may lag behind during rebuilds).
    /// </summary>
    private static async Task<Dictionary<Guid, Principal>> LoadUsersAsync(
        IQuerySession session, IEnumerable<Guid> principalIds)
    {
        var principals = await session.LoadManyAsync<Principal>(CancellationToken.None, principalIds.Distinct());
        return principals.ToDictionary(p => p.Id);
    }

    /// <summary>
    /// Build a ViewRef for a principal — Id, display label (Acronym | Firstname Lastname
    /// for humans, GroupName for groups) and PrincipalType for the UI.
    /// </summary>
    private static ViewRef BuildPrincipalRef(Dictionary<Guid, Principal> principals, Guid id)
    {
        principals.TryGetValue(id, out var p);
        return new ViewRef
        {
            Id = id,
            Label = p?.DisplayName,
            PrincipalType = p?.Type,
        };
    }
}
