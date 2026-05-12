using System.Text.Json.Serialization;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authorization.Apps;
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

            // RealmSigningKey lives in the per-tenant store (configured below),
            // not here. Defense-in-depth: a master-DB compromise must NOT leak
            // every realm's private signing key — the key for realm A only sits
            // in realm A's database. Stage 2 of the realm-key-isolation plan;
            // stage 3 (encryption at rest) lands later.

            opts.UseSystemTextJsonForSerialization(configure: o =>
            {
                o.PropertyNamingPolicy = null;
                o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                o.Converters.Add(new JsonStringEnumConverter());
            });
        }).ApplyAllDatabaseChangesOnStartup();

        // Tenancy services
        services.AddSingleton<IMasterConnectionString>(new MasterConnectionString(connectionString));
        // Marten's BuildSessionsWith<T> registers T only against ISessionFactory in
        // 8.x — not as the concrete type itself. The OpenIddict Marten stores resolve
        // the factory via ITenantSessionFactory, which forwards to the concrete class
        // below; without an explicit concrete registration, /connect/token (and any
        // other code path that touches the OpenIddict stores from a Wolverine
        // handler / outside an HTTP request) crashes with "no service for type
        // TenantedSessionFactory has been registered".
        services.AddSingleton<TenantedSessionFactory>();
        services.AddSingleton<ITenantSessionFactory>(sp => sp.GetRequiredService<TenantedSessionFactory>());

        // Per-realm signing keys (C3b — multi-tenancy crypto isolation).
        // Singleton so the in-memory cache survives across requests; reads of
        // active credentials run on every token issuance.
        services.AddSingleton<IRealmKeyStore, RealmKeyStore>();
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

        // Cocoar.Auth.Authorization — runtime services (IPermissionService,
        // IMembershipEvaluator, IPrincipalEmailResolver, IPrincipalLookupService,
        // IAutoMembershipRecalculator) + the resource registry. Sub-class mapping +
        // STJ polymorphism + Marten event aliases happen via UseCocoarAuthAuthorization
        // on the StoreOptions (see ConfigureDocumentStore).
        services.AddCocoarAuthAuthorization(opt =>
        {
            // Cocoar.Auth is an Identity Provider — its admin surface lives under
            // the system app `cocoar-auth`. Permissions are validated as
            // <app>:<resource>:<action>, so this registry's keys are
            // (appSlug, resource) → actions.
            //
            // Bypasses recognised by PermissionEvaluator (no entry needed here):
            //   - realm:admin                  — realm-wide bypass
            //   - <app>:admin                  — bypass within a single app
            //   - cocoar-auth:oauth:admin      — full OAuth admin surface
            //   - cocoar-auth:login-provider:admin — login-provider admin surface
            const string app = AppSlugs.CocoarAuth;

            // Apps themselves — the realm-admin surface for registering and
            // editing Application records (one per Cocoar SaaS app onboarded
            // into this realm). The system app cocoar-auth is seeded
            // automatically and cannot be deleted.
            opt.RegisterResource(app, "app", "admin", "read", "write");

            // Identity / directory
            opt.RegisterResource(app, "user", "read", "write");
            opt.RegisterResource(app, "role", "read", "write");
            opt.RegisterResource(app, "authorization-group", "read", "write");
            opt.RegisterResource(app, "permission-role", "read", "write");

            // Sessions + audit
            opt.RegisterResource(app, "session", "read", "write");
            opt.RegisterResource(app, "auth-log", "read");

            // GDPR (permanent-erase only — self-service is implicit on the caller)
            opt.RegisterResource(app, "gdpr", "admin");

            // OAuth admin surface — granular AND a kept-as-bypass `oauth:admin`.
            opt.RegisterResource(app, "oauth", "admin");
            opt.RegisterResource(app, "oauth-client", "read", "write");
            opt.RegisterResource(app, "oauth-scope", "read", "write");
            opt.RegisterResource(app, "oauth-api", "read", "write");

            // Login providers (the configurable buttons on the login page)
            opt.RegisterResource(app, "login-provider", "admin", "read", "write");

            // Realm-wide settings (self-registration etc.) — owned by the
            // realm-admin, not Control-Plane. The realm-admin gets these
            // via the `realm:admin` bypass; granular grants are also
            // supported. See Authentication/Api/Admin/RealmSettingsEndpoints.
            opt.RegisterResource(app, "realm-settings", "read", "write");

            // ── control-plane app — cross-realm administration surface ─────────
            // Resources under this slug are ONLY mounted on the configured
            // Control-Plane hostname (see ControlPlaneGateMiddleware in
            // Cocoar.Auth.Api). Tenant realms get 404 on these routes and
            // also can't grant the permissions because the app isn't seeded
            // into their tenant DB (see AppRealmSeeder).
            const string controlPlaneApp = AppSlugs.ControlPlane;
            opt.RegisterResource(controlPlaneApp, "realm", "read", "write");
        });

        // OAuth admin slice services — both consume the tenant-scoped IDocumentSession
        // injected by TenantedSessionFactory, so calls land in the correct realm DB.
        services.AddScoped<OAuthAdminService>();

        // Register JsEval (Linq-enabled + Principal discriminator mappings
        // for Type.Is() in membership scripts).
        //
        // Cocoar.JsEval 4.0 made the engine surface safe-by-default:
        // NewObject, require, exit, setTimeout/Interval/clear*, console,
        // and the __log_* bridge are all OFF unless explicitly enabled
        // via the corresponding `Enable…()` builder flag. Membership
        // scripts are pure predicates over Person/Group/ServiceAccount —
        // none of those globals are needed, so we just don't enable them.
        //
        // `WithExecutionTimeout(2s)` is a defense-in-depth backstop for
        // runaway top-level scripts (`while(true){}` etc.). It pairs
        // with the consumer-side wall-clock budget in
        // MembershipEvaluator.BuildPredicate, which also bounds the
        // synchronous translator phase that the engine timeout cannot
        // reach.
        //
        // See website/testing/jseval-threat-model.md for the full
        // engine-globals catalog and the rationale per global.
        services.AddJsEval(b => b
            .AddLinq()
            .AddDiscriminatorMappings<Principal>("Type",
                ("person", typeof(Person)),
                ("group", typeof(Group)),
                ("service-account", typeof(ServiceAccount)))
            .WithExecutionTimeout(TimeSpan.FromSeconds(2)));
        services.AddTsTranspiler();
        services.AddTsDefinition();

        return services;
    }
}
