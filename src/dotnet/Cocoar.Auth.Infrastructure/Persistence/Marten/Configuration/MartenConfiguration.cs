using Cocoar.Auth.Authorization.Setup;
using JasperFx;
using JasperFx.Events;
using Marten;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Domain.Users.Events;
using Weasel.Core;

namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Configuration;

public static class MartenConfiguration
{
    /// <summary>
    /// Enables multi-tenancy with the master-table strategy: tenant DBs are
    /// registered in a <c>realms.mt_tenant_databases</c> table that lives in
    /// the master DB (= the connection string passed to <c>AddInfrastructure</c>).
    /// Each tenant gets its own PostgreSQL database; per-realm provisioning is
    /// handled by <c>RealmProvisioningService</c>.
    /// </summary>
    public static void UseMasterTableMultiTenancy(this StoreOptions options, string masterConnectionString)
    {
        options.MultiTenantedDatabasesWithMasterDatabaseTable(x =>
        {
            x.ConnectionString = masterConnectionString;
            x.SchemaName = "realms";
            x.AutoCreate = AutoCreate.CreateOrUpdate;
            x.ApplicationName = "CocoarAuth";
        });
    }

    public static void ConfigureDocumentStore(this StoreOptions options)
    {
        // Use System.Text.Json (first-class in Marten 8+, consistent with API + SignalR).
        // Compose app-specific customization (Optional<T> aware) with the auth slice's
        // Principal polymorphism resolver in a single configure call — calling
        // UseSystemTextJsonForSerialization twice discards the earlier configuration.
        options.UseSystemTextJsonForSerialization(
            enumStorage: EnumStorage.AsString,
            configure: o =>
            {
                o.AddOptionalAware();
                o.AddCocoarAuthAuthorizationPolymorphism();
            });

        // Cocoar.Auth.Authorization — Principal sub-class mapping, PermissionRole schema,
        // PermissionRoleProjection, auth event-type aliases. Must run AFTER
        // UseSystemTextJsonForSerialization above so the existing configured serializer
        // is extended (not replaced).
        options.UseCocoarAuthAuthorization();

        // Per-tenant RealmSigningKey storage. Keys live IN the tenant DB (not
        // the master DB) so a master-DB compromise — or a Realm registry leak
        // — cannot expose another realm's private signing material. There's
        // exactly one realm's keys per database, so the table stays small;
        // no slug index needed (the database itself is the index).
        // Stage 3 of the realm-key-isolation plan (encryption at rest with a
        // process-level master key) lands as a follow-up.
        options.Schema.For<Cocoar.Auth.Domain.Realms.RealmSigningKey>()
            .Identity(x => x.Id);

        // C4 — Server-side consent ticket. Per-tenant DB; tickets are bound
        // to the calling user's subject so cross-tenant misuse is impossible
        // by construction. Indexed on ExpiresAt for the future janitor that
        // trims expired-and-consumed records.
        options.Schema.For<Cocoar.Auth.Domain.OAuth.Consent.ConsentTicket>()
            .Identity(x => x.Id)
            .Index(x => x.ExpiresAt);

        // DataProtection keys (cookie/antiforgery encryption material).
        // Lives in the system tenant so every instance shares one key pool
        // and keys survive Container-Restart. Friendly-name (UUID-shaped,
        // framework-supplied) is the stable document key. See
        // Cocoar.Auth.Infrastructure.Persistence.DataProtection.
        options.Schema.For<Cocoar.Auth.Infrastructure.Persistence.DataProtection.DataProtectionKeyDocument>()
            .Identity(x => x.Id);

        // Tenant-scoped asset library (logos, favicons, login illustrations).
        // Indexed on UploadedAt for the admin-list "newest first" ordering.
        options.Schema.For<Cocoar.Auth.Domain.Assets.Asset>()
            .Identity(x => x.Id)
            .Index(x => x.UploadedAt);

        // Quartz scheduling — per-tenant config overrides + run history.
        // JobConfig keys are string-typed (registration keys, e.g. "dcr-gc").
        // JobRunHistoryEntry is append-only; admin UI shows the last N
        // per job-key. Indexed on (JobKey, StartedAt) for the descending
        // history scan that drives the Last Run column + history tab.
        options.Schema.For<Cocoar.Auth.Infrastructure.Scheduling.JobConfig>()
            .Identity(x => x.Key);
        options.Schema.For<Cocoar.Auth.Infrastructure.Scheduling.JobRunHistoryEntry>()
            .Identity(x => x.Id)
            .Index(x => new { x.JobKey, x.StartedAt });

        // Inbox retention settings — singleton per tenant (fixed id), admin-tunable.
        // The async InboxItemView projection is registered via
        // UseCocoarAuthAuthentication() so the composite ViewProjections daemon
        // picks it up alongside UserViewProjection.
        options.Schema.For<Cocoar.Auth.Application.Inbox.InboxRetentionSettings>()
            .Identity(x => x.Id);

        // Authentication-specific Marten setup (documents + events + projections)
        // is wired via UseCocoarAuthAuthentication(), called from AddInfrastructure's
        // additionalMartenConfig callback so Infrastructure stays unaware of Authentication.
    }

    public static void ConfigureEventStore(this StoreOptions options)
    {
        options.Events.StreamIdentity = StreamIdentity.AsGuid;

        // Critter-Stack 2026 backport: pin two Marten 9 event-store defaults
        // back to V8 behaviour. Walked back individually (instead of the blanket
        // opts.RestoreV8Defaults()) so each entry can be re-evaluated on its own
        // merits later. See C:\git\cocoar\Cocoar.AppBase\docs\migrations\v1-to-v2.md
        // § "Schritt 5".
        //
        // AppendMode: 9.x default `QuickWithServerTimestamps` delegates metadata
        // stamping to a Postgres function — can shift ordering/version timing.
        // Our RaiseSideEffects overrides on UserViewProjection (and any future
        // self-mutating projection) assume Rich-timing.
        options.Events.AppendMode = EventAppendMode.Rich;
        //
        // UseIdentityMapForAggregates: 9.x default `true` can leak self-mutations
        // via events within the same batch. Cocoar.Auth's principal projection
        // and view projections rely on snapshot freshness — keep V8's `false`.
        options.Events.UseIdentityMapForAggregates = false;

        // User profile events (kept here because the Authentication Principal projection
        // bridges them into the unified Principal document table — see CocoarAuthPrincipalProjection).
        // Aliases are decoupled from CLR type names — safe to refactor namespaces.
        options.Events.MapEventType<UserCreatedEvent>("user_created");
        options.Events.MapEventType<UserUpdatedEvent>("user_updated");
        options.Events.MapEventType<UserDeletedEvent>("user_deleted");
        options.Events.MapEventType<UserMigratedEvent>("user_migrated");

        // Authorization slice events (PermissionRole + Group + GroupMembership) are
        // registered by UseCocoarAuthAuthorization() — kept inside the slice so apps
        // that adopt it get the aliases for free.

        // Authentication slice events (identity + IdP + ExternalAuth) are registered
        // by UseCocoarAuthAuthentication() — called via additionalMartenConfig callback.

        // Principal projection, user-view projection, and the ViewProjections composite
        // are registered by UseCocoarAuthAuthentication() — that slice owns all projections
        // that depend on identity events.
    }
}
