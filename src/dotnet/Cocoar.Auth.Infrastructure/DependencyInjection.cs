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
        // ───── Security lockdown ─────
        //
        // cocoar.js-eval wires a number of engine globals by default. For
        // membership scripts we keep only what's needed and strip the rest
        // via a post-init engine configurator (configurators run AFTER
        // JsEngine.Initialize()). The full surface inventory + reasoning is
        // in website/testing/jseval-threat-model.md (Engine-globals table).
        //
        // KEPT (membership-script useful, low/no risk):
        //   - Type     — discriminator narrowing (`Type.Is(p, 'person')`)
        //   - linq     — typed-literal helpers (`linq.guid("…")`)
        //   - btoa, atob, __perf_now, performance, __te_encode/decode,
        //     TextEncoder/Decoder, structuredClone — pure conversions
        //
        // STRIPPED (every one closes a real or potential surface):
        //   - NewObject, require        (host-RCE — arbitrary CLR-type
        //                                construction via assembly walk;
        //                                see Gap-5 / A2-NewObject)
        //   - exit                      (engine-DoS — `exit()` cancels the
        //                                engine's CancellationToken and
        //                                future recomputes on the same
        //                                Scoped engine fail)
        //   - setTimeout, setInterval,
        //     clearTimeout, clearInterval (schedules callbacks on the
        //                                shared TaskScheduler — async
        //                                pollution, unbounded background
        //                                work outliving the recompute)
        //   - __log_info/_warn/_error/_debug, console
        //                               (log-spam — admin-authored scripts
        //                                shouldn't be able to flood the
        //                                ops log infrastructure; `console.log`
        //                                in scripts becomes a no-op /
        //                                ReferenceError after this)
        //
        // Pinned by tests in
        // Cocoar.Auth.Tests.Unit/Authorization/MembershipSecurityTests.cs
        // (A2 group). Removing or weakening any item below should turn a
        // pinning test red.
        services.AddJsEval(b => b
            .AddLinq()
            .AddDiscriminatorMappings<Principal>("Type",
                ("person", typeof(Person)),
                ("group", typeof(Group)),
                ("service-account", typeof(ServiceAccount)))
            .RegisterEngineConfigurator(engine =>
            {
                var undef = Jint.Native.JsValue.Undefined;
                // Host-RCE primitives
                engine.SetValue("NewObject", undef);
                engine.SetValue("require", undef);
                // Engine-DoS
                engine.SetValue("exit", undef);
                // Async / TaskScheduler pollution
                engine.SetValue("setTimeout", undef);
                engine.SetValue("setInterval", undef);
                engine.SetValue("clearTimeout", undef);
                engine.SetValue("clearInterval", undef);
                // Log-spam vector
                engine.SetValue("console", undef);
                engine.SetValue("__log_info", undef);
                engine.SetValue("__log_warn", undef);
                engine.SetValue("__log_error", undef);
                engine.SetValue("__log_debug", undef);
            }));
        services.AddTsTranspiler();
        services.AddTsDefinition();

        return services;
    }
}
