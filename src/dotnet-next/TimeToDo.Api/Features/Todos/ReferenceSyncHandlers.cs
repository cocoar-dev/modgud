using TimeToDo.Authorization.Principals;
using Marten;
using Marten.Patching;
using TimeToDo.Api.Features.Shared;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.Todos;

public class TodoViewUserLabelSyncHandler(ILogger<TodoViewUserLabelSyncHandler> logger)
    : ReferenceSyncHandler<UserUpdatedEvent>(logger)
{
    protected override bool ShouldSync(UserUpdatedEvent @event)
        => @event.Firstname.HasValue || @event.Lastname.HasValue || @event.Acronym.HasValue;

    protected override async Task SyncAsync(UserUpdatedEvent @event, IDocumentSession session)
    {
        var user = await session.LoadAsync<Principal>(@event.Id);
        if (user is null) return;

        var newLabel = user.DisplayName;
        Logger.LogInformation("[TodoView:UserLabelSync] Label='{NewLabel}' for user {UserId}", newLabel, @event.Id);

        session.Patch<TodoView>(t => t.CreatedBy != null && t.CreatedBy.Id == @event.Id && !t.IsDeleted)
            .Set(t => t.CreatedBy!.Label, newLabel);

        session.Patch<TodoView>(t => t.UpdatedBy != null && t.UpdatedBy.Id == @event.Id && !t.IsDeleted)
            .Set(t => t.UpdatedBy!.Label, newLabel);

        var todosWithResponsible = await session.Query<TodoView>()
            .Where(t => !t.IsDeleted && t.Responsibles.Any(r => r.Id == @event.Id))
            .ToListAsync();

        foreach (var todo in todosWithResponsible)
        {
            var updatedResponsibles = todo.Responsibles
                .Select(r => r.Id == @event.Id ? r with { Label = newLabel } : r)
                .ToList();
            session.Patch<TodoView>(todo.Id).Set(t => t.Responsibles, updatedResponsibles);
        }
    }
}

public class TodoViewCustomerLabelSyncHandler(ILogger<TodoViewCustomerLabelSyncHandler> logger)
    : ReferenceSyncHandler<CustomerUpdatedEvent>(logger)
{
    protected override bool ShouldSync(CustomerUpdatedEvent @event)
        => @event.Name.HasValue;

    protected override async Task SyncAsync(CustomerUpdatedEvent @event, IDocumentSession session)
    {
        var customer = await session.LoadAsync<CustomerValidationData>(@event.Id);
        if (customer is null) return;

        Logger.LogInformation("[TodoView:CustomerLabelSync] Name='{NewName}' for customer {CustomerId}", customer.Name, @event.Id);

        session.Patch<TodoView>(t => t.Customer != null && t.Customer.Id == @event.Id && !t.IsDeleted)
            .Set(t => t.Customer!.Label, customer.Name);
    }
}
