using Modgud.Infrastructure.FollowUps;

namespace Modgud.Infrastructure.PositionTerminals;

/// <summary>
/// G-B4-02 — the follow-up of a staffing session end: revoke the session's OAuth authorization.
/// <see cref="StaffingRevoker"/> writes it in the SAME commit as <c>StaffingSessionEnded</c>, so an
/// ended session can no longer keep a live authorization because the revoke after the commit
/// failed. The handler is idempotent (a revoked or deleted authorization is done).
/// </summary>
public sealed record RevokeStaffingAuthorization(Guid StaffingSessionId, string AuthorizationId) : IFollowUpMessage;
