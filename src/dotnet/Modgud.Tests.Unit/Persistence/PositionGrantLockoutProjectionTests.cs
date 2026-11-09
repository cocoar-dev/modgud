using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.Persistence.Marten.Projections.PositionTerminals;

namespace Modgud.Tests.Unit.Persistence;

/// <summary>
/// G-B4-06 — a proof failure records the lock its writer computed from the failure count it had
/// read. A failure computed from an older count carries no lock; applied after the failure that set
/// one, it cleared that lock until the next failure. A failure never shortens or clears a lock; only
/// a success does.
/// </summary>
public class PositionGrantLockoutProjectionTests
{
    private static readonly Guid GrantId = Guid.NewGuid();

    [Fact]
    public void A_late_failure_without_a_lock_keeps_the_lock_an_earlier_failure_set()
    {
        var projection = new PositionGrantProjection();
        var grant = new PositionGrant { Id = GrantId };
        var lockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);

        projection.Apply(new PositionGrantActivationFailed(GrantId, DateTimeOffset.UtcNow, lockedUntil), grant);
        projection.Apply(new PositionGrantActivationFailed(GrantId, DateTimeOffset.UtcNow, null), grant);

        Assert.Equal(2, grant.ActivationFailedCount);
        Assert.Equal(lockedUntil, grant.ActivationLockoutEnd);
    }

    [Fact]
    public void A_later_lock_extends_and_a_success_clears()
    {
        var projection = new PositionGrantProjection();
        var grant = new PositionGrant { Id = GrantId };
        var first = DateTimeOffset.UtcNow.AddMinutes(5);
        var later = first.AddMinutes(10);

        projection.Apply(new PositionGrantActivationFailed(GrantId, DateTimeOffset.UtcNow, first), grant);
        projection.Apply(new PositionGrantActivationFailed(GrantId, DateTimeOffset.UtcNow, later), grant);
        Assert.Equal(later, grant.ActivationLockoutEnd);

        projection.Apply(new PositionGrantActivationSucceeded(GrantId, DateTimeOffset.UtcNow), grant);
        Assert.Null(grant.ActivationLockoutEnd);
        Assert.Equal(0, grant.ActivationFailedCount);
    }
}
