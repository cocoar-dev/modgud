using Marten.Events.Aggregation;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.PositionTerminals;

/// <summary>
/// Builds <see cref="StaffingSession"/> documents inline from session streams
/// (MG-FT-05).
/// </summary>
public partial class StaffingSessionProjection : SingleStreamProjection<StaffingSession, Guid>
{
    public StaffingSession Create(StaffingSessionStarted e) => new()
    {
        Id = e.Id,
        PositionPrincipalId = e.PositionPrincipalId,
        TerminalEnrollmentId = e.TerminalEnrollmentId,
        ActivatedByUserId = e.ActivatedByUserId,
        ActivatedByPasskeyCredentialId = e.ActivatedByPasskeyCredentialId,
        PositionGrantId = e.PositionGrantId,
        DpopJkt = e.DpopJkt,
        OAuthAuthorizationId = e.OAuthAuthorizationId,
        Status = StaffingSessionStatus.Active,
        StartedAt = e.StartedAt,
        AbsoluteExpiresAt = e.AbsoluteExpiresAt,
    };

    public void Apply(StaffingSessionEnded e, StaffingSession session)
    {
        session.Status = StaffingSessionStatus.Ended;
        session.EndedAt = e.EndedAt;
        session.EndReason = e.Reason;
    }
}
