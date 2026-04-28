using System.Text.Json.Serialization;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Setup;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TsDefinition;
using Cocoar.JsEval.TypeScript;
using JasperFx;
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
        // Make sure HttpContext access is available — TenantedSessionFactory
        // needs it to resolve the active tenant out of HttpContext.Items.
        services.AddHttpContextAccessor();

        // Configure Marten — multi-tenant master-table strategy. Each realm has its
        // own PostgreSQL DB (`{mainDb}_{slug}`); the master DB hosts the tenant
        // registry table (`realms.mt_tenant_databases`) plus the global Realm store.
        // Auth slice's sub-class mapping + STJ polymorphism + event aliases all live
        // inside UseCocoarAuthAuthorization() / the AddCocoarAuthAuthorizationPolymorphism
        // call inside the STJ configure lambda (see ConfigureDocumentStore).
        var martenBuilder = services.AddMarten(options =>
        {
            // NOTE: do NOT call options.Connection(connectionString) when using
            // master-table multi-tenancy — Marten resolves connection strings per-tenant
            // out of the master table.
            options.UseMasterTableMultiTenancy(connectionString);
            options.ConfigureDocumentStore();
            additionalMartenConfig?.Invoke(options);
            options.ConfigureEventStore();
        })
        // BuildSessionsWith installs our TenantedSessionFactory as the singleton
        // ISessionFactory. Every IDocumentSession / IQuerySession injection now
        // resolves the tenant from HttpContext.Items["TenantId"] (set by RealmMiddleware),
        // falling back to the "system" tenant when no HttpContext is available.
        // NOTE: this replaces the previous .UseLightweightSessions() call — our factory
        // also returns LightweightSession()-backed sessions.
        // Singleton lifetime: the factory is stateless — IHttpContextAccessor (Singleton)
        // gives a fresh HttpContext per call, IDocumentStore (Singleton) is reused.
        // Required so Wolverine's Singleton OutboxedSessionFactory can consume it
        // without DEV-mode scope-validation failing.
        .BuildSessionsWith<TenantedSessionFactory>(ServiceLifetime.Singleton);

        // Schema migrations are applied manually during bootstrap (after the system
        // tenant has been registered). Calling .ApplyAllDatabaseChangesOnStartup()
        // here would race the tenancy registration: at host-start the master table
        // exists but no tenant DBs are registered, so the per-tenant schema apply
        // would no-op and never recover for the system tenant.

        // Register the global (non-tenanted) DocumentStore for cross-tenant data.
        // Uses the same physical DB as the master table — but a separate Marten
        // store so the schemas don't collide with tenant content.
        services.AddMartenStore<IGlobalStore>(opts =>
        {
            opts.Connection(connectionString);
            opts.DatabaseSchemaName = "global";
            opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            opts.Schema.For<Realm>()
                .Identity(x => x.Id)
                .Index(x => x.Slug, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsActive')::boolean = true)"; });

            opts.UseSystemTextJsonForSerialization(configure: o =>
            {
                o.PropertyNamingPolicy = null;
                o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                o.Converters.Add(new JsonStringEnumConverter());
            });
        }).ApplyAllDatabaseChangesOnStartup();

        // Tenancy services
        services.AddSingleton<IMasterConnectionString>(new MasterConnectionString(connectionString));
        services.AddSingleton<ITenantSessionFactory>(sp => sp.GetRequiredService<TenantedSessionFactory>());
        services.AddSingleton<IRealmCache, RealmCache>();
        services.AddScoped<IRealmProvisioningService, RealmProvisioningService>();

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
            // Cocoar.Auth is an Identity Provider. The full admin surface is split into
            // per-resource read/write pairs so a "User Manager", "Help-Desk", or
            // "Viewer" role can be granted in isolation. Global bypass roles:
            //   - app:admin     — system-wide admin (kept for backwards compatibility,
            //                     bypasses every check in PermissionService)
            //   - oauth:admin   — full OAuth admin surface
            //   - login-provider:admin — login-provider admin surface
            opt.RegisterResource("app", "admin");

            // Identity / directory
            opt.RegisterResource("user", "read", "write");
            opt.RegisterResource("role", "read", "write");
            opt.RegisterResource("authorization-group", "read", "write");
            opt.RegisterResource("permission-role", "read", "write");

            // Sessions + audit
            opt.RegisterResource("session", "read", "write");
            opt.RegisterResource("auth-log", "read");

            // GDPR (permanent-erase only — self-service is implicit on the caller)
            opt.RegisterResource("gdpr", "admin");

            // Realms (multi-tenant management — only meaningful in tenant-management realms)
            opt.RegisterResource("realm", "read", "write");

            // Identity-provider configs (external OIDC IdPs)
            opt.RegisterResource("idp-config", "read", "write");

            // OAuth admin surface — granular AND a kept-as-bypass `oauth:admin`.
            opt.RegisterResource("oauth", "admin");
            opt.RegisterResource("oauth-client", "read", "write");
            opt.RegisterResource("oauth-scope", "read", "write");
            opt.RegisterResource("oauth-api", "read", "write");

            // Login providers (the configurable buttons on the login page)
            opt.RegisterResource("login-provider", "admin", "read", "write");
        });

        // OAuth admin slice services — both consume the tenant-scoped IDocumentSession
        // injected by TenantedSessionFactory, so calls land in the correct realm DB.
        services.AddScoped<OAuthAdminService>();
        services.AddScoped<LoginProviderService>();

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
