using Modgud.Domain.PositionTerminals;

namespace Modgud.Infrastructure.PositionTerminals;

/// <summary>
/// Ends position staffing sessions outside their natural token flow (MG-FT-07,
/// plan §15.1): local/remote lock and every revocation cascade (user, passkey,
/// grant, terminal, position) funnel through here. Each operation selects only
/// ACTIVE sessions, appends the <see cref="StaffingSessionEnded"/> event with
/// the given reason, clears the terminal's <c>ActiveStaffingSessionId</c> when
/// it still points at the ending session, revokes exactly that session's
/// OpenIddict authorization (reference tokens die instantly), records a
/// security-audit entry, and is idempotent — ending an already-ended session
/// is a no-op, never an error.
///
/// <para>Placement note (deviation from the plan's Application-layer sketch):
/// lives in Infrastructure because it composes Marten, the OpenIddict grant
/// revoker and the audit log — and because the Authentication slice (passkey
/// deletion cascade) references Infrastructure, not Application.</para>
/// </summary>
public interface IStaffingRevoker
{
    /// <summary>Ends one session. Returns 1 when it was active, else 0.</summary>
    Task<int> EndSessionAsync(Guid sessionId, StaffingSessionEndReason reason, CancellationToken ct = default);

    Task<int> EndAllForTerminalAsync(Guid terminalId, StaffingSessionEndReason reason, CancellationToken ct = default);

    Task<int> EndAllForPositionAsync(Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default);

    Task<int> EndAllForUserAndPositionAsync(Guid userId, Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default);

    Task<int> EndAllForUserAsync(Guid userId, StaffingSessionEndReason reason, CancellationToken ct = default);

    Task<int> EndAllForPasskeyAsync(Guid credentialId, StaffingSessionEndReason reason, CancellationToken ct = default);

    Task<int> EndAllForGrantAsync(Guid grantId, StaffingSessionEndReason reason, CancellationToken ct = default);
    Task<int> EndAllForActivationTokenAsync(Guid activationTokenId, StaffingSessionEndReason reason, CancellationToken ct = default);
    Task<int> EndAllForActivationTokenAndPositionAsync(Guid activationTokenId, Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default);
}
