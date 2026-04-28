using Marten;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Api.Features.Migration;

/// <summary>
/// Runs on startup to migrate existing documents to event streams.
/// Idempotent — skips entities that already have a stream.
/// Can be removed once all environments are migrated.
/// </summary>
public class EventStoreMigrationService(IServiceScopeFactory scopeFactory, ILogger<EventStoreMigrationService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        await MigrateUsers(session, cancellationToken);
        await MigrateCustomers(session, cancellationToken);
        await MigrateTodos(session, cancellationToken);
        await MigrateComments(session, cancellationToken);
        // Labels (CreatedBy, Customer, Responsibles) are denormalized in TodoView/CommentView.
        // During normal operation + rebuilds, Stage 1 (UserView/CustomerView) completes before
        // Stage 2 (TodoView/CommentView), so labels are always resolved from the final UserView state.
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateUsers(IDocumentSession session, CancellationToken ct)
    {
        var documents = await session.Query<UserDocument>().ToListAsync(ct);
        var migrated = 0;
        var migratedAt = DateTime.UtcNow;

        foreach (var doc in documents)
        {
            var existingStream = await session.Events.FetchStreamStateAsync(doc.Id, ct);
            if (existingStream != null)
                continue;

            var migratedEvent = new UserMigratedEvent(
                doc.Id,
                Firstname: doc.Firstname ?? "",
                Lastname: doc.Lastname ?? "",
                Acronym: doc.Acronym ?? "",
                Email: doc.Email ?? "",
                MigratedAt: migratedAt
            );
            session.Events.StartStream<UserView>(doc.Id, migratedEvent);
            migrated++;
        }

        if (migrated > 0)
        {
            await session.SaveChangesAsync(ct);
            logger.LogInformation("EventStore migration: created {Count} User event streams from existing documents", migrated);
        }
    }

    private async Task MigrateCustomers(IDocumentSession session, CancellationToken ct)
    {
        var documents = await session.Query<CustomerDocument>().ToListAsync(ct);
        var migrated = 0;
        var migratedAt = DateTime.UtcNow;

        foreach (var doc in documents)
        {
            var existingStream = await session.Events.FetchStreamStateAsync(doc.Id, ct);
            if (existingStream != null)
                continue;

            var migratedEvent = new CustomerMigratedEvent(
                doc.Id,
                Name: doc.Name,
                IsImportant: doc.IsImportant,
                IsArchived: doc.IsArchived,
                MigratedAt: migratedAt
            );
            session.Events.StartStream<CustomerView>(doc.Id, migratedEvent);
            migrated++;
        }

        if (migrated > 0)
        {
            await session.SaveChangesAsync(ct);
            logger.LogInformation("EventStore migration: created {Count} Customer event streams from existing documents", migrated);
        }
    }

    private async Task MigrateTodos(IDocumentSession session, CancellationToken ct)
    {
        var documents = await session.Query<TodoDocument>().ToListAsync(ct);
        var migrated = 0;
        var migratedAt = DateTime.UtcNow;

        foreach (var doc in documents)
        {
            var existingStream = await session.Events.FetchStreamStateAsync(doc.Id, ct);
            if (existingStream != null)
                continue;

            var migratedEvent = new TodoMigratedEvent(
                doc.Id,
                Title: doc.Title,
                Description: doc.Description,
                DueDate: doc.DueDate,
                Status: Enum.Parse<TodoStatus>(doc.Status, ignoreCase: true),
                CustomerId: doc.CustomerId,
                ResponsibleUserIds: doc.ResponsibleUserIds,
                ParentTodoId: doc.ParentTodoId,
                ChildTodoIds: doc.ChildTodoIds,
                IsArchived: doc.IsArchived,
                IsCritical: doc.IsCritical,
                IsAwaitingFeedback: doc.IsAwaitingFeedback,
                CommentsCount: doc.CommentsCount,
                CreatedAt: doc.CreatedAt,
                CreatedById: doc.CreatedById,
                UpdatedAt: doc.UpdatedAt,
                UpdatedById: doc.UpdatedById,
                MigratedAt: migratedAt
            );
            session.Events.StartStream<TodoView>(doc.Id, migratedEvent);
            migrated++;
        }

        if (migrated > 0)
        {
            await session.SaveChangesAsync(ct);
            logger.LogInformation("EventStore migration: created {Count} Todo event streams from existing documents", migrated);
        }
    }

    private async Task MigrateComments(IDocumentSession session, CancellationToken ct)
    {
        var documents = await session.Query<CommentDocument>().ToListAsync(ct);
        var migrated = 0;
        var migratedAt = DateTime.UtcNow;

        // Pre-load all read statuses, grouped by CommentId
        var allReadStatuses = await session.Query<CommentReadStatusDocument>().ToListAsync(ct);
        var readStatusesByCommentId = allReadStatuses
            .GroupBy(rs => rs.CommentId)
            .ToDictionary(g => g.Key, g => g.Select(rs => rs.UserId).Distinct().ToList());

        foreach (var doc in documents)
        {
            var existingStream = await session.Events.FetchStreamStateAsync(doc.Id, ct);
            if (existingStream != null)
                continue;

            var migratedEvent = new CommentMigratedEvent(
                doc.Id,
                Description: doc.Description,
                ReferencedItemId: doc.ReferencedItemId,
                ReferencedItemType: doc.ReferencedItemType,
                CreatedAt: doc.CreatedAt,
                CreatedById: doc.CreatedById,
                UpdatedAt: doc.UpdatedAt,
                UpdatedById: doc.UpdatedById,
                MigratedAt: migratedAt
            );

            var readByUserIds = readStatusesByCommentId.GetValueOrDefault(doc.Id, new List<Guid>());
            var events = new List<object> { migratedEvent };
            foreach (var userId in readByUserIds)
            {
                events.Add(new CommentMarkedAsReadEvent(doc.Id, userId, migratedAt));
            }

            session.Events.StartStream<CommentView>(doc.Id, events.ToArray());
            migrated++;
        }

        if (migrated > 0)
        {
            await session.SaveChangesAsync(ct);
            logger.LogInformation("EventStore migration: created {Count} Comment event streams from existing documents", migrated);
        }
    }

}
