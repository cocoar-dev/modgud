using Marten.Events.Aggregation;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Projections;

/// <summary>
/// Builds <see cref="PositionPrincipal"/> documents inline from position
/// streams. PositionPrincipal is mapped as a concrete subclass of Principal,
/// so Marten stores the result in the shared principal table while this
/// projection stays independent from Person and Group events.
/// </summary>
public partial class PositionPrincipalProjection : SingleStreamProjection<PositionPrincipal, Guid>
{
    public PositionPrincipalProjection()
    {
        // PositionPrincipal shares mt_doc_principal with Person, Group, and the
        // (still) non-event-sourced ServiceAccount subtype. A normal Marten
        // teardown would truncate that entire root table; cleanup is coordinated
        // explicitly by the authentication slice's PrincipalProjectionRebuilder.
        Options.TeardownDataOnRebuild = false;

        // Defining this constructor suppresses the source generator's generated
        // IncludeType constructor, so keep the event allow-list explicit here.
        IncludeType<PositionPrincipalCreatedEvent>();
        IncludeType<PositionPrincipalUpdatedEvent>();
        IncludeType<PositionPrincipalDeletedEvent>();
    }

    // Apply the creation event even when a snapshot already exists — during a
    // teardown-free rebuild this replaces the old snapshot with a fresh
    // PositionPrincipal before the remainder of the stream is replayed.
    public PositionPrincipal Apply(PositionPrincipalCreatedEvent @event, PositionPrincipal _) => new()
    {
        Id = @event.Id,
        AccountName = @event.AccountName,
        Purpose = @event.Purpose,
        IsActive = @event.IsActive,
        TerminalPolicy = @event.TerminalPolicy,
        IsDeleted = false,
    };

    public PositionPrincipal Apply(PositionPrincipalUpdatedEvent @event, PositionPrincipal fn)
    {
        fn.AccountName = @event.AccountName;
        fn.Purpose = @event.Purpose;
        fn.IsActive = @event.IsActive;
        fn.TerminalPolicy = @event.TerminalPolicy;
        return fn;
    }

    public PositionPrincipal Apply(PositionPrincipalDeletedEvent @event, PositionPrincipal fn)
    {
        fn.IsDeleted = true;
        fn.IsActive = false;
        return fn;
    }
}
