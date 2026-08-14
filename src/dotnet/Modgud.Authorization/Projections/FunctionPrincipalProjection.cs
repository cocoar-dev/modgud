using Marten.Events.Aggregation;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Projections;

/// <summary>
/// Builds <see cref="FunctionPrincipal"/> documents inline from function
/// streams. FunctionPrincipal is mapped as a concrete subclass of Principal,
/// so Marten stores the result in the shared principal table while this
/// projection stays independent from Person and Group events.
/// </summary>
public partial class FunctionPrincipalProjection : SingleStreamProjection<FunctionPrincipal, Guid>
{
    public FunctionPrincipalProjection()
    {
        // FunctionPrincipal shares mt_doc_principal with Person, Group, and the
        // (still) non-event-sourced ServiceAccount subtype. A normal Marten
        // teardown would truncate that entire root table; cleanup is coordinated
        // explicitly by the authentication slice's PrincipalProjectionRebuilder.
        Options.TeardownDataOnRebuild = false;

        // Defining this constructor suppresses the source generator's generated
        // IncludeType constructor, so keep the event allow-list explicit here.
        IncludeType<FunctionPrincipalCreatedEvent>();
        IncludeType<FunctionPrincipalUpdatedEvent>();
        IncludeType<FunctionPrincipalDeletedEvent>();
    }

    // Apply the creation event even when a snapshot already exists — during a
    // teardown-free rebuild this replaces the old snapshot with a fresh
    // FunctionPrincipal before the remainder of the stream is replayed.
    public FunctionPrincipal Apply(FunctionPrincipalCreatedEvent @event, FunctionPrincipal _) => new()
    {
        Id = @event.Id,
        AccountName = @event.AccountName,
        Purpose = @event.Purpose,
        IsActive = @event.IsActive,
        TerminalPolicy = @event.TerminalPolicy,
        IsDeleted = false,
    };

    public FunctionPrincipal Apply(FunctionPrincipalUpdatedEvent @event, FunctionPrincipal fn)
    {
        fn.AccountName = @event.AccountName;
        fn.Purpose = @event.Purpose;
        fn.IsActive = @event.IsActive;
        fn.TerminalPolicy = @event.TerminalPolicy;
        return fn;
    }

    public FunctionPrincipal Apply(FunctionPrincipalDeletedEvent @event, FunctionPrincipal fn)
    {
        fn.IsDeleted = true;
        fn.IsActive = false;
        return fn;
    }
}
