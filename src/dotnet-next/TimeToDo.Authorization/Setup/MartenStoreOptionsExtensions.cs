using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JasperFx.Events.Projections;
using Marten;
using TimeToDo.Authorization.Events;
using TimeToDo.Authorization.Principals;
using TimeToDo.Authorization.Projections;
using TimeToDo.Authorization.Roles;

namespace TimeToDo.Authorization.Setup;

/// <summary>
/// Marten / STJ wiring for the authorization slice. Hardcoded for TimeToDo's
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
    /// <see cref="AddTimeTodoAuthorizationPolymorphism"/> inside the consumer's
    /// <c>UseSystemTextJsonForSerialization</c> <c>configure</c> lambda so STJ
    /// polymorphism composes with the rest of the serializer setup.
    /// </para>
    /// </summary>
    public static StoreOptions UseTimeTodoAuthorization(this StoreOptions martenOpts)
    {
        // Polymorphic Principal table — Person + Group both land in mt_doc_principal,
        // distinguished by mt_doc_type. Aliases are stable across class renames.
        martenOpts.Schema.For<Principal>()
            .AddSubClass<Person>("person")
            .AddSubClass<Group>("group");

        // PermissionRole — its own top-level document.
        martenOpts.Schema.For<PermissionRole>();

        // Inline role projection — owned by the lib.
        martenOpts.Projections.Add<PermissionRoleProjection>(ProjectionLifecycle.Inline);

        // Stable event-type aliases. Marten resolves events through these, so the
        // .NET type FQN can change without breaking persisted streams.
        martenOpts.Events.MapEventType<GroupCreatedEvent>("authorization_group_created");
        martenOpts.Events.MapEventType<GroupUpdatedEvent>("authorization_group_updated");
        martenOpts.Events.MapEventType<GroupDeletedEvent>("authorization_group_deleted");
        martenOpts.Events.MapEventType<GroupMembershipRecomputedEvent>("authorization_group_membership_recomputed");
        martenOpts.Events.MapEventType<GroupMembershipRecomputeFailedEvent>("authorization_group_membership_recompute_failed");

        martenOpts.Events.MapEventType<PermissionRoleCreatedEvent>("permission_role_created");
        martenOpts.Events.MapEventType<PermissionRoleUpdatedEvent>("permission_role_updated");
        martenOpts.Events.MapEventType<PermissionRoleDeletedEvent>("permission_role_deleted");

        return martenOpts;
    }

    /// <summary>
    /// Registers STJ polymorphism for <see cref="Principal"/> with hardcoded
    /// derived types (<see cref="Person"/> as <c>"person"</c>,
    /// <see cref="Group"/> as <c>"group"</c>). Call inside the Marten
    /// <c>UseSystemTextJsonForSerialization</c> <c>configure</c> lambda so the
    /// modifier composes with other STJ customizations (e.g. <c>AddOptionalAware()</c>).
    /// </summary>
    public static void AddTimeTodoAuthorizationPolymorphism(this JsonSerializerOptions opts)
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
    }
}
