using TimeToDo.Authorization.Setup;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using TimeToDo.Domain.Common;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using Weasel.Core;

namespace TimeToDo.Infrastructure.Persistence.Marten.Configuration;

public static class MartenConfiguration
{
    public static void ConfigureDocumentStore(this StoreOptions options)
    {
        // Configure automatic schema management (Marten 8.x)
        options.Schema.For<UserDocument>().DatabaseSchemaName("marten");
        options.Schema.For<CustomerDocument>().DatabaseSchemaName("marten");
        options.Schema.For<TodoDocument>().DatabaseSchemaName("marten");
        options.Schema.For<CommentDocument>().DatabaseSchemaName("marten");
        options.Schema.For<CommentReadStatusDocument>().DatabaseSchemaName("marten");

        // Use System.Text.Json (first-class in Marten 8+, consistent with API + SignalR).
        // Compose app-specific customization (Optional<T> aware) with the auth slice's
        // Principal polymorphism resolver in a single configure call — calling
        // UseSystemTextJsonForSerialization twice discards the earlier configuration.
        options.UseSystemTextJsonForSerialization(
            enumStorage: EnumStorage.AsString,
            configure: o =>
            {
                o.AddOptionalAware();
                o.AddTimeTodoAuthorizationPolymorphism();
            });

        // Configure non-auth documents
        ConfigureUserDocument(options);
        ConfigureCustomerDocument(options);
        ConfigureTodoDocument(options);
        ConfigureCommentDocument(options);
        ConfigureCommentReadStatusDocument(options);

        // TimeToDo.Authorization — Principal sub-class mapping, PermissionRole schema,
        // PermissionRoleProjection, auth event-type aliases. Must run AFTER
        // UseSystemTextJsonForSerialization above so the existing configured serializer
        // is extended (not replaced).
        options.UseTimeTodoAuthorization();

        // Authentication-specific Marten setup (documents + events + projections)
        // is wired via UseTimeTodoAuthentication(), called from AddInfrastructure's
        // additionalMartenConfig callback so Infrastructure stays unaware of Authentication.
    }

    private static void ConfigureUserDocument(StoreOptions options)
    {
        options.Schema.For<UserDocument>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.Email)
            .Index(x => x.Acronym);
    }

    private static void ConfigureCustomerDocument(StoreOptions options)
    {
        options.Schema.For<CustomerDocument>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .UniqueIndex(x => x.Name)
            .Index(x => x.IsArchived)
            .Index(x => x.IsImportant);
    }

    private static void ConfigureTodoDocument(StoreOptions options)
    {
        options.Schema.For<TodoDocument>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.Status)
            .Index(x => x.IsArchived)
            .Index(x => x.IsCritical)
            .Index(x => x.IsAwaitingFeedback)
            .Index(x => x.CustomerId)
            .Index(x => x.ParentTodoId)
            .Index(x => x.DueDate)
            .Index(x => x.CreatedAt)
            .Index(x => x.CreatedById);
    }

    private static void ConfigureCommentDocument(StoreOptions options)
    {
        options.Schema.For<CommentDocument>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.ReferencedItemId)
            .Index(x => x.ReferencedItemType)
            .Index(x => x.CreatedAt);
    }

    private static void ConfigureCommentReadStatusDocument(StoreOptions options)
    {
        options.Schema.For<CommentReadStatusDocument>()
            .Identity(x => x.Id)
            .Index(x => x.CommentId)
            .Index(x => x.UserId)
            .Index(x => x.ReadAt);
    }

    public static void ConfigureEventStore(this StoreOptions options)
    {
        options.Events.StreamIdentity = StreamIdentity.AsGuid;

        // Event type aliases (decoupled from CLR type names — safe to refactor namespaces)
        options.Events.MapEventType<UserCreatedEvent>("user_created");
        options.Events.MapEventType<UserUpdatedEvent>("user_updated");
        options.Events.MapEventType<UserDeletedEvent>("user_deleted");
        options.Events.MapEventType<UserMigratedEvent>("user_migrated");

        options.Events.MapEventType<CustomerCreatedEvent>("customer_created");
        options.Events.MapEventType<CustomerUpdatedEvent>("customer_updated");
        options.Events.MapEventType<CustomerDeletedEvent>("customer_deleted");
        options.Events.MapEventType<CustomerMigratedEvent>("customer_migrated");

        options.Events.MapEventType<TodoCreatedEvent>("todo_created");
        options.Events.MapEventType<TodoUpdatedEvent>("todo_updated");
        options.Events.MapEventType<TodoDeletedEvent>("todo_deleted");
        options.Events.MapEventType<TodoMigratedEvent>("todo_migrated");
        options.Events.MapEventType<TodoStatusChangedEvent>("todo_status_changed");
        options.Events.MapEventType<TodoFlagsChangedEvent>("todo_flags_changed");
        options.Events.MapEventType<TodoArchivedEvent>("todo_archived");
        options.Events.MapEventType<TodoChildAddedEvent>("todo_child_added");
        options.Events.MapEventType<TodoChildRemovedEvent>("todo_child_removed");
        options.Events.MapEventType<TodoParentChangedEvent>("todo_parent_changed");
        options.Events.MapEventType<TodoCommentsCountChangedEvent>("todo_comments_count_changed");

        options.Events.MapEventType<CommentCreatedEvent>("comment_created");
        options.Events.MapEventType<CommentDeletedEvent>("comment_deleted");
        options.Events.MapEventType<CommentMigratedEvent>("comment_migrated");
        options.Events.MapEventType<CommentMarkedAsReadEvent>("comment_marked_as_read");

        // Authorization slice events (PermissionRole + Group + GroupMembership) are
        // registered by UseTimeTodoAuthorization() — kept inside the slice so apps
        // that adopt it get the aliases for free.

        // Authentication slice events (identity + IdP + ExternalAuth) are registered
        // by UseTimeTodoAuthentication() — called via additionalMartenConfig callback.

        // Validation projections - always inline (immediate consistency for command validation)
        options.Projections.Add<CustomerValidationProjection>(ProjectionLifecycle.Inline);
        options.Projections.Add<TodoValidationProjection>(ProjectionLifecycle.Inline);

        // Principal projection, user-view projection, and the ViewProjections composite
        // are registered by UseTimeTodoAuthentication() — that slice owns all projections
        // that depend on identity events.
    }
}
