using Marten.Events.Aggregation;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.PositionTerminals;

/// <summary>
/// Builds <see cref="PositionGrant"/> documents inline from grant
/// streams (MG-FT-02). Own top-level document table — unlike the principal
/// projections there is no shared-table teardown concern.
/// </summary>
public partial class PositionGrantProjection : SingleStreamProjection<PositionGrant, Guid>
{
    public PositionGrant Create(PositionGrantIssued e) => new()
    {
        Id = e.Id,
        PositionPrincipalId = e.PositionPrincipalId,
        UserId = e.UserId,
        Status = PositionGrantStatus.Active,
        CreatedAt = e.IssuedAt,
        CreatedByUserId = e.IssuedByUserId,
    };

    // B3-P1 — Revoked is terminal. A suspend or resume that read the grant before an
    // overlapping revoke committed can still land after it in the stream; it must not
    // revive the grant (the re-grant path is a fresh issue).
    public void Apply(PositionGrantSuspended e, PositionGrant grant)
    {
        if (grant.Status != PositionGrantStatus.Revoked)
            grant.Status = PositionGrantStatus.Suspended;
    }

    public void Apply(PositionGrantResumed e, PositionGrant grant)
    {
        if (grant.Status != PositionGrantStatus.Revoked)
            grant.Status = PositionGrantStatus.Active;
    }

    public void Apply(PositionGrantRevoked e, PositionGrant grant)
    {
        grant.Status = PositionGrantStatus.Revoked;
        grant.RevokedAt = e.RevokedAt;
        grant.RevokedByUserId = e.RevokedByUserId;
    }

    /// <summary>G-B4-06 — the writer computes <c>LockedUntil</c> from the failure count it read, so a
    /// failure recorded from an older count carries no lock. A failure therefore never shortens or
    /// clears a lock (the later end wins); only a success does.</summary>
    public void Apply(PositionGrantActivationFailed e, PositionGrant grant)
    {
        grant.ActivationFailedCount++;
        if (e.LockedUntil is { } lockedUntil
            && (grant.ActivationLockoutEnd is not { } current || lockedUntil > current))
            grant.ActivationLockoutEnd = lockedUntil;
    }

    public void Apply(PositionGrantActivationSucceeded e, PositionGrant grant)
    {
        grant.ActivationFailedCount = 0;
        grant.ActivationLockoutEnd = null;
    }
}
