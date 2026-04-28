using Marten;
using Marten.Patching;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Projections;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Api.Features.Migration;

public static class MigrationEndpoints
{
    public static WebApplication MapMigrationEndpoints(this WebApplication application, string path)
    {
        var migrationGroup = application.MapGroup($"{path}/migration")
            .WithTags("Migration")
            .RequireAuthorization()
            .RequiresPermission("app:admin");

        migrationGroup.MapPost("users", async (IDocumentSession session) =>
        {
            var documents = await session.Query<UserDocument>().ToListAsync();
            var migrated = 0;
            var skipped = 0;
            var migratedAt = DateTime.UtcNow;

            foreach (var doc in documents)
            {
                var existingView = await session.LoadAsync<UserView>(doc.Id);
                if (existingView != null)
                {
                    skipped++;
                    continue;
                }

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

            await session.SaveChangesAsync();

            return Results.Ok(new { Migrated = migrated, Skipped = skipped, Total = documents.Count });
        }).WithName("Migration_Users");

        migrationGroup.MapPost("customers", async (IDocumentSession session) =>
        {
            var documents = await session.Query<CustomerDocument>().ToListAsync();
            var migrated = 0;
            var skipped = 0;
            var migratedAt = DateTime.UtcNow;

            foreach (var doc in documents)
            {
                var existingView = await session.LoadAsync<CustomerView>(doc.Id);
                if (existingView != null)
                {
                    skipped++;
                    continue;
                }

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

            await session.SaveChangesAsync();

            return Results.Ok(new { Migrated = migrated, Skipped = skipped, Total = documents.Count });
        }).WithName("Migration_Customers");

        migrationGroup.MapPost("todos", async (IDocumentSession session) =>
        {
            var documents = await session.Query<TodoDocument>().ToListAsync();
            var migrated = 0;
            var skipped = 0;
            var migratedAt = DateTime.UtcNow;

            foreach (var doc in documents)
            {
                var existingView = await session.LoadAsync<TodoView>(doc.Id);
                if (existingView != null)
                {
                    skipped++;
                    continue;
                }

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

            await session.SaveChangesAsync();

            return Results.Ok(new { Migrated = migrated, Skipped = skipped, Total = documents.Count });
        }).WithName("Migration_Todos");

        migrationGroup.MapPost("comments", async (IDocumentSession session) =>
        {
            var documents = await session.Query<CommentDocument>().ToListAsync();
            var migrated = 0;
            var skipped = 0;
            var migratedAt = DateTime.UtcNow;

            // Pre-load all read statuses, grouped by CommentId
            var allReadStatuses = await session.Query<CommentReadStatusDocument>().ToListAsync();
            var readStatusesByCommentId = allReadStatuses
                .GroupBy(rs => rs.CommentId)
                .ToDictionary(g => g.Key, g => g.Select(rs => rs.UserId).Distinct().ToList());

            foreach (var doc in documents)
            {
                var existingView = await session.LoadAsync<CommentView>(doc.Id);
                if (existingView != null)
                {
                    skipped++;
                    continue;
                }

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

            await session.SaveChangesAsync();

            return Results.Ok(new { Migrated = migrated, Skipped = skipped, Total = documents.Count });
        }).WithName("Migration_Comments");

        migrationGroup.MapPost("populate-labels", async (IDocumentSession session) =>
        {
            var users = (await session.Query<UserView>().ToListAsync()).ToDictionary(u => u.Id);
            var customers = (await session.Query<CustomerView>().ToListAsync()).ToDictionary(c => c.Id);

            var todosUpdated = 0;
            var commentsUpdated = 0;

            // Patch TodoViews — use Patch API (session.Store() is a no-op on async projection documents)
            var todos = await session.Query<TodoView>().Where(t => !t.IsDeleted).ToListAsync();
            foreach (var todo in todos)
            {
                if (todo.Customer?.Id is { } cid && customers.TryGetValue(cid, out var c) && c.Name != todo.Customer.Label)
                {
                    session.Patch<TodoView>(todo.Id).Set(t => t.Customer!.Label, c.Name);
                    todosUpdated++;
                }

                if (todo.CreatedBy?.Id is { } cbId && users.TryGetValue(cbId, out var cu))
                {
                    var newLabel = cu.GetDisplayLabel();
                    if (newLabel != todo.CreatedBy.Label)
                        session.Patch<TodoView>(todo.Id).Set(t => t.CreatedBy!.Label, newLabel);
                }

                if (todo.UpdatedBy?.Id is { } ubId && users.TryGetValue(ubId, out var uu))
                {
                    var newLabel = uu.GetDisplayLabel();
                    if (newLabel != todo.UpdatedBy.Label)
                        session.Patch<TodoView>(todo.Id).Set(t => t.UpdatedBy!.Label, newLabel);
                }

                var needsResponsibleUpdate = todo.Responsibles.Any(r =>
                    users.TryGetValue(r.Id, out var ru) && ru.GetDisplayLabel() != r.Label);
                if (needsResponsibleUpdate)
                {
                    var updatedResponsibles = todo.Responsibles
                        .Select(r => users.TryGetValue(r.Id, out var ru)
                            ? r with { Label = ru.GetDisplayLabel() }
                            : r)
                        .ToList();
                    session.Patch<TodoView>(todo.Id).Set(t => t.Responsibles, updatedResponsibles);
                }
            }

            // Patch CommentViews
            var comments = await session.Query<CommentView>().Where(c => !c.IsDeleted).ToListAsync();
            foreach (var comment in comments)
            {
                if (comment.CreatedBy?.Id is { } commentCreatedById && users.TryGetValue(commentCreatedById, out var u))
                {
                    var newLabel = u.GetDisplayLabel();
                    if (newLabel != comment.CreatedBy.Label)
                    {
                        session.Patch<CommentView>(comment.Id).Set(c => c.CreatedBy!.Label, newLabel);
                        commentsUpdated++;
                    }
                }
            }

            await session.SaveChangesAsync();

            return Results.Ok(new { TodosUpdated = todosUpdated, CommentsUpdated = commentsUpdated });
        }).WithName("Migration_PopulateLabels");

        return application;
    }
}
