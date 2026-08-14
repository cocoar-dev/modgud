using Marten.Events.Aggregation;
using Modgud.Domain.FunctionTerminals;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.FunctionTerminals;

/// <summary>
/// Builds <see cref="FunctionActivationGrant"/> documents inline from grant
/// streams (MG-FT-02). Own top-level document table — unlike the principal
/// projections there is no shared-table teardown concern.
/// </summary>
public partial class FunctionActivationGrantProjection : SingleStreamProjection<FunctionActivationGrant, Guid>
{
    public FunctionActivationGrant Create(FunctionActivationGrantIssued e) => new()
    {
        Id = e.Id,
        FunctionPrincipalId = e.FunctionPrincipalId,
        UserId = e.UserId,
        Status = FunctionActivationGrantStatus.Active,
        CreatedAt = e.IssuedAt,
        CreatedByUserId = e.IssuedByUserId,
    };

    public void Apply(FunctionActivationGrantSuspended e, FunctionActivationGrant grant)
        => grant.Status = FunctionActivationGrantStatus.Suspended;

    public void Apply(FunctionActivationGrantResumed e, FunctionActivationGrant grant)
        => grant.Status = FunctionActivationGrantStatus.Active;

    public void Apply(FunctionActivationGrantRevoked e, FunctionActivationGrant grant)
    {
        grant.Status = FunctionActivationGrantStatus.Revoked;
        grant.RevokedAt = e.RevokedAt;
        grant.RevokedByUserId = e.RevokedByUserId;
    }
}
