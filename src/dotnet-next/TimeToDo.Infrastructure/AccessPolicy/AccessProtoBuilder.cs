using Marten;
using TimeToDo.Infrastructure.Persistence.Marten.Projections;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Infrastructure.AccessPolicy;

/// <summary>
/// Builds a view-shaped "proto" object from a create DTO before the document exists,
/// so access scripts can evaluate the create attempt against the same shape they
/// evaluate for read/update. This prevents the "create a todo for a customer you
/// can't see" leak the coupled model can't catch by itself.
/// </summary>
public interface IAccessProtoBuilder
{
    Task<TodoView> BuildTodoProtoAsync(
        string title,
        string? description,
        DateTime? dueDate,
        Domain.ValueObjects.TodoStatus status,
        Guid? customerId,
        List<Guid> responsibleUserIds,
        bool isCritical,
        bool isAwaitingFeedback,
        Guid? parentTodoId,
        Guid createdById,
        CancellationToken ct = default);

    CustomerView BuildCustomerProto(string? name, bool isImportant);
}

public class AccessProtoBuilder(IQuerySession session) : IAccessProtoBuilder
{
    public async Task<TodoView> BuildTodoProtoAsync(
        string title,
        string? description,
        DateTime? dueDate,
        Domain.ValueObjects.TodoStatus status,
        Guid? customerId,
        List<Guid> responsibleUserIds,
        bool isCritical,
        bool isAwaitingFeedback,
        Guid? parentTodoId,
        Guid createdById,
        CancellationToken ct = default)
    {
        // Inherit customer from parent when creating a subtodo — matches CreateTodoHandler
        // behavior so the proto reflects the same row shape that will be persisted.
        if (parentTodoId.HasValue)
        {
            var parent = await session.LoadAsync<TodoView>(parentTodoId.Value, ct);
            if (parent is not null && !parent.IsDeleted)
                customerId = parent.Customer?.Id;
        }

        ViewRef? customerRef = null;
        if (customerId.HasValue)
        {
            var customer = await session.LoadAsync<CustomerView>(customerId.Value, ct);
            if (customer is not null)
                customerRef = new ViewRef { Id = customer.Id, Label = customer.Name };
        }

        var responsibles = new List<ViewRef>();
        if (responsibleUserIds.Count > 0)
        {
            var users = await session.Query<UserView>()
                .Where(u => u.Id.IsOneOf(responsibleUserIds.ToArray()) && !u.IsDeleted)
                .ToListAsync(ct);
            responsibles = users.Select(u => new ViewRef
            {
                Id = u.Id,
                Label = u.Acronym ?? $"{u.Firstname} {u.Lastname}".Trim(),
                PrincipalType = "Person",
            }).ToList();
        }

        var creator = await session.LoadAsync<UserView>(createdById, ct);
        var creatorRef = creator is null
            ? null
            : new ViewRef
            {
                Id = creator.Id,
                Label = creator.Acronym ?? $"{creator.Firstname} {creator.Lastname}".Trim(),
                PrincipalType = "Person",
            };

        return new TodoView
        {
            Id = Guid.Empty, // not yet persisted
            Title = title,
            Description = description,
            DueDate = dueDate,
            Status = status,
            Customer = customerRef,
            Responsibles = responsibles,
            ParentTodoId = parentTodoId,
            ChildTodoIds = [],
            IsArchived = false,
            IsCritical = isCritical,
            IsAwaitingFeedback = isAwaitingFeedback,
            CommentsCount = 0,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = creatorRef,
            UpdatedAt = null,
            UpdatedBy = null,
            IsDeleted = false,
        };
    }

    public CustomerView BuildCustomerProto(string? name, bool isImportant) => new()
    {
        Id = Guid.Empty,
        Name = name,
        IsImportant = isImportant,
        IsArchived = false,
        IsDeleted = false,
    };
}
