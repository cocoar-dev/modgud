using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Setup;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TsDefinition;
using Cocoar.JsEval.TypeScript;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Application.Contracts;
using Cocoar.Auth.Infrastructure.Events;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Configuration;
using Wolverine.Marten;

namespace Cocoar.Auth.Infrastructure;

public static class DependencyInjection
{
    /// <param name="additionalMartenConfig">
    /// Optional callback to wire additional Marten setup (e.g. authentication slice's
    /// <c>UseCocoarAuthAuthentication()</c>) without creating a dependency from
    /// Infrastructure → Authentication. Called between ConfigureDocumentStore and
    /// ConfigureEventStore so STJ is already set up when auth documents are registered.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        Action<StoreOptions>? additionalMartenConfig = null)
    {
        // Configure Marten — auth slice's sub-class mapping + STJ polymorphism +
        // event aliases all live inside UseCocoarAuthAuthorization() / the
        // AddCocoarAuthAuthorizationPolymorphism call inside the STJ configure lambda
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

        // Register Event Dispatcher
        services.AddScoped<IEventDispatcher, SignalREventDispatcher>();

        // Cocoar.Auth.Authorization — runtime services (IPermissionService, IAccessPolicyEngine,
        // IMembershipEvaluator, IPrincipalEmailResolver, IPrincipalLookupService,
        // IAutoMembershipRecalculator) + the resource registry. Sub-class mapping +
        // STJ polymorphism + Marten event aliases happen via UseCocoarAuthAuthorization
        // on the StoreOptions (see ConfigureDocumentStore).
        services.AddCocoarAuthAuthorization(opt =>
        {
            // Cocoar.Auth is an Identity Provider — only the global admin resource
            // is registered out of the box. App-specific resources (todo, customer, …)
            // are added by the host app's adoption layer.
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

        return services;
    }
}
