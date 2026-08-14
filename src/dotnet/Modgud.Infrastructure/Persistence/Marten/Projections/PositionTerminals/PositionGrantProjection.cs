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

    public void Apply(PositionGrantSuspended e, PositionGrant grant)
        => grant.Status = PositionGrantStatus.Suspended;

    public void Apply(PositionGrantResumed e, PositionGrant grant)
        => grant.Status = PositionGrantStatus.Active;

    public void Apply(PositionGrantRevoked e, PositionGrant grant)
    {
        grant.Status = PositionGrantStatus.Revoked;
        grant.RevokedAt = e.RevokedAt;
        grant.RevokedByUserId = e.RevokedByUserId;
    }
}
