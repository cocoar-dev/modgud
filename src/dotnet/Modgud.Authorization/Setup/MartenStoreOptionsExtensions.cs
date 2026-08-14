using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JasperFx.Events.Projections;
using Marten;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;
using Modgud.Authorization.Roles;

namespace Modgud.Authorization.Setup;

/// <summary>
/// Marten / STJ wiring for the authorization slice. Hardcoded for Modgud's
/// concrete principal hierarchy (<see cref="Person"/> + <see cref="Group"/>) —
/// when adopting this slice into another app, copy + adjust the sub-class
/// list and aliases here.
/// </summary>
public static class MartenStoreOptionsExtensions
{
    /// <summary>
    /// Wires the authorization schema into a Marten <see cref="StoreOptions"/>:
    /// <list type="bullet">
    ///   <item>Sub-class mapping for the polymorphic <see cref="Principal"/> table
    ///         (Person + Group with stable aliases).</item>
    ///   <item><see cref="PermissionRole"/> document + its inline projection.</item>
    ///   <item>Stable event-type aliases for all auth events — keeps
    ///         <c>mt_events.type</c> rename-proof.</item>
    /// </list>
    /// <para>
    /// Does NOT touch serializer configuration — call
    /// <see cref="AddModgudAuthorizationPolymorphism"/> inside the consumer's
    /// <c>UseSystemTextJsonForSerialization</c> <c>configure</c> lambda so STJ
    /// polymorphism composes with the rest of the serializer setup.
    /// </para>
    /// </summary>
    public static StoreOptions UseModgudAuthorization(this StoreOptions martenOpts)
    {
        // Polymorphic Principal table — Person + Group + ServiceAccount all land
        // in mt_doc_principal, distinguished by mt_doc_type. Aliases are stable
        // across class renames. ServiceAccount inherits from Principal so the
        // BFS group-membership graph + the polymorphic /api/principal/lookup
        // see machine identities alongside humans without per-table joins.
        martenOpts.Schema.For<Principal>()
            // Principal has always used the bigint revision written by its
            // event projections. With separate concrete Person/Group projections,
            // Marten 9.23 can otherwise infer Guid optimistic concurrency for the
            // shared base table and generate an impossible bigint -> uuid ALTER on
            // existing installations. Keep the projection revision explicit and
            // backwards-compatible.
            .UseNumericRevisions(true)
            .AddSubClass<Person>("person")
            .AddSubClass<Group>("group")
            .AddSubClass<ServiceAccount>("service-account")
            .AddSubClass<FunctionPrincipal>("function");

        // PermissionRole — its own top-level document.
        martenOpts.Schema.For<PermissionRole>();

        // App — logical scope inside a realm (the user-facing concept is
        // "Application"; the class is `App` to avoid collision with the
        // Modgud.Application CQRS-layer namespace). Indexed by Slug for
        // the common "find app by slug" lookup and by IsDeleted to keep admin
        // queries fast.
        martenOpts.Schema.For<App>()
            .Index(x => x.Slug)
            .Index(x => x.IsDeleted);

        // Inline projections — admin reads + slug-uniqueness checks need
        // synchronous consistency. GroupProjection targets the concrete Group
        // subtype directly; Marten stores it in the shared Principal hierarchy.
        martenOpts.Projections.Add<PermissionRoleProjection>(ProjectionLifecycle.Inline);
        martenOpts.Projections.Add<AppProjection>(ProjectionLifecycle.Inline);
        martenOpts.Projections.Add<GroupProjection>(ProjectionLifecycle.Inline);
        martenOpts.Projections.Add<FunctionPrincipalProjection>(ProjectionLifecycle.Inline);

        // Stable event-type aliases. Marten resolves events through these, so the
        // .NET type FQN can change without breaking persisted streams.
        martenOpts.Events.MapEventType<GroupCreatedEvent>("authorization_group_created");
        martenOpts.Events.MapEventType<GroupUpdatedEvent>("authorization_group_updated");
        martenOpts.Events.MapEventType<GroupDeletedEvent>("authorization_group_deleted");
        martenOpts.Events.MapEventType<GroupMembershipRecomputedEvent>("authorization_group_membership_recomputed");
        martenOpts.Events.MapEventType<GroupMembershipRecomputeFailedEvent>("authorization_group_membership_recompute_failed");

        martenOpts.Events.MapEventType<FunctionPrincipalCreatedEvent>("authorization_function_created");
        martenOpts.Events.MapEventType<FunctionPrincipalUpdatedEvent>("authorization_function_updated");
        martenOpts.Events.MapEventType<FunctionPrincipalDeletedEvent>("authorization_function_deleted");

        martenOpts.Events.MapEventType<PermissionRoleCreatedEvent>("permission_role_created");
        martenOpts.Events.MapEventType<PermissionRoleUpdatedEvent>("permission_role_updated");
        martenOpts.Events.MapEventType<PermissionRoleDeletedEvent>("permission_role_deleted");

        martenOpts.Events.MapEventType<AppCreatedEvent>("authorization_app_created");
        martenOpts.Events.MapEventType<AppUpdatedEvent>("authorization_app_updated");
        martenOpts.Events.MapEventType<AppDeletedEvent>("authorization_app_deleted");

        return martenOpts;
    }

    /// <summary>
    /// Registers STJ polymorphism for <see cref="Principal"/> with hardcoded
    /// derived types (<see cref="Person"/> as <c>"person"</c>,
    /// <see cref="Group"/> as <c>"group"</c>). Call inside the Marten
    /// <c>UseSystemTextJsonForSerialization</c> <c>configure</c> lambda so the
    /// modifier composes with other STJ customizations (e.g. <c>AddOptionalAware()</c>).
    /// </summary>
    public static void AddModgudAuthorizationPolymorphism(this JsonSerializerOptions opts)
    {
        if (opts.TypeInfoResolver is not DefaultJsonTypeInfoResolver resolver)
        {
            resolver = new DefaultJsonTypeInfoResolver();
            opts.TypeInfoResolver = resolver;
        }
        resolver.Modifiers.Add(ApplyPrincipalPolymorphism);
    }

    private static void ApplyPrincipalPolymorphism(JsonTypeInfo ti)
    {
        if (ti.Type != typeof(Principal)) return;

        ti.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$type",
            IgnoreUnrecognizedTypeDiscriminators = false,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(Person), "person"));
        ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(Group), "group"));
        ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(ServiceAccount), "service-account"));
        ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(FunctionPrincipal), "function"));
    }
}
