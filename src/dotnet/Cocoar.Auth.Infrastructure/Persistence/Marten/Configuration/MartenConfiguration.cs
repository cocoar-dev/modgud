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

        // Authentication-specific Marten setup (documents + events + projections)
        // is wired via UseCocoarAuthAuthentication(), called from AddInfrastructure's
        // additionalMartenConfig callback so Infrastructure stays unaware of Authentication.
    }

    public static void ConfigureEventStore(this StoreOptions options)
    {
        options.Events.StreamIdentity = StreamIdentity.AsGuid;

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
