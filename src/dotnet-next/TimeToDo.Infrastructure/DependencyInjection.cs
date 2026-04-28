using TimeToDo.Authorization.Principals;
using TimeToDo.Authorization.Setup;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TsDefinition;
using Cocoar.JsEval.TypeScript;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Application.Contracts;
using TimeToDo.Domain.DomainServices;
using TimeToDo.Domain.Repositories;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Configuration;
using TimeToDo.Infrastructure.Persistence.Marten.Repositories;
using TimeToDo.Infrastructure.QueryServices;
using Wolverine.Marten;

namespace TimeToDo.Infrastructure;

public static class DependencyInjection
{
    /// <param name="additionalMartenConfig">
    /// Optional callback to wire additional Marten setup (e.g. authentication slice's
    /// <c>UseTimeTodoAuthentication()</c>) without creating a dependency from
    /// Infrastructure → Authentication. Called between ConfigureDocumentStore and
    /// ConfigureEventStore so STJ is already set up when auth documents are registered.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        Action<StoreOptions>? additionalMartenConfig = null)
    {
        // Configure Marten — auth slice's sub-class mapping + STJ polymorphism +
        // event aliases all live inside UseTimeTodoAuthorization() / the
        // AddTimeTodoAuthorizationPolymorphism call inside the STJ configure lambda
        // (see ConfigureDocumentStore).
        var martenBuilder = services.AddMarten(options =>
        {
            options.Connection(connectionString);
            options.ConfigureDocumentStore();
            additionalMartenConfig?.Invoke(options);
            options.ConfigureEventStore();
        })
        .UseLightweightSessions()
        .ApplyAllDatabaseChangesOnStartup();

        // Required for Marten projection side effects to publish messages via Wolverine
        // EventForwardingToWolverine: forwards domain events as Wolverine messages on commit
        martenBuilder.IntegrateWithWolverine(options =>
        {
            options.UseFastEventForwarding = true;
        });

        martenBuilder.AddAsyncDaemon(DaemonMode.Solo);

        // Register Domain Services
        services.AddScoped<TodoHierarchyService>();

        // Register Repository implementations
        services.AddScoped<ITodoRepository, MartenTodoRepository>();
        services.AddScoped<ICustomerRepository, MartenCustomerRepository>();
        services.AddScoped<IUserRepository, MartenUserRepository>();
        services.AddScoped<ICommentRepository, MartenCommentRepository>();

        // Register Query Services (read-side)
        services.AddScoped<ITodoQueryService, MartenTodoQueryService>();
        services.AddScoped<ICustomerQueryService, MartenCustomerQueryService>();
        services.AddScoped<IUserQueryService, MartenUserQueryService>();
        services.AddScoped<ICommentQueryService, MartenCommentQueryService>();

        // Register Event Dispatcher
        services.AddScoped<IEventDispatcher, SignalREventDispatcher>();

        // TimeToDo.Authorization — runtime services (IPermissionService, IAccessPolicyEngine,
        // IMembershipEvaluator, IPrincipalEmailResolver, IPrincipalLookupService,
        // IAutoMembershipRecalculator) + the resource registry. Sub-class mapping +
        // STJ polymorphism + Marten event aliases happen via UseTimeTodoAuthorization
        // on the StoreOptions (see ConfigureDocumentStore).
        services.AddTimeTodoAuthorization(opt =>
        {
            opt.RegisterResource("todo", "read", "create", "update", "delete", "archive", "restore", "flag", "move");
            opt.RegisterResource("customer", "read", "create", "update", "delete", "archive", "restore");
            opt.RegisterResource("comment", "read", "create", "delete");
            opt.RegisterResource("app", "admin");
        });

        // Register JsEval (Linq-enabled + Principal discriminator mappings for Type.Is() in membership scripts)
        services.AddJsEval(b => b
            .AddLinq()
            .AddDiscriminatorMappings<Principal>("Type",
                ("person", typeof(Person)),
                ("group", typeof(Group)),
                ("service-account", typeof(ServiceAccount))));
        services.AddTsTranspiler();
        services.AddTsDefinition();
        services.AddScoped<IAccessPolicyEngine, AccessPolicyEngine>();
        services.AddScoped<IAccessProtoBuilder, AccessProtoBuilder>();
        services.AddScoped<IAuthorizationSimulator, AuthorizationSimulator>();

        return services;
    }
}
