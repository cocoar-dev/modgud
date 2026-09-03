using Microsoft.Extensions.DependencyInjection;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.PositionTerminals;

/// <summary>
/// Apply-scope-aware <see cref="IStaffingRevoker"/> (ADR-0005 Phase 0): outside a
/// <see cref="TenantApplyTransaction"/> every call passes straight through to
/// <see cref="StaffingRevoker"/>. Inside one, ending staffing sessions is a
/// CONSEQUENCE of a staged config change (policy tightening, deactivation, grant
/// revocation, prune) and is deferred until after the transaction committed; on
/// rollback it never runs. Deferred calls re-resolve the concrete revoker from a
/// fresh scope, so its own sessions, audit entries, bus messages and nested token
/// revocations all run against committed state. The ended-session counts are not
/// knowable at defer time — deferred methods report 0; apply-path callers ignore
/// them (the operations are idempotent: ending an ended session is a no-op).
/// </summary>
public sealed class DeferringStaffingRevoker(StaffingRevoker inner) : IStaffingRevoker
{
    public Task<int> EndSessionAsync(Guid sessionId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing session '{sessionId}' ({reason})", ct,
            (r, c) => r.EndSessionAsync(sessionId, reason, c));

    public Task<int> EndAllForTerminalAsync(Guid terminalId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for terminal '{terminalId}' ({reason})", ct,
            (r, c) => r.EndAllForTerminalAsync(terminalId, reason, c));

    public Task<int> EndAllForPositionAsync(Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for position '{positionId}' ({reason})", ct,
            (r, c) => r.EndAllForPositionAsync(positionId, reason, c));

    public Task<int> EndAllForUserAndPositionAsync(Guid userId, Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for user '{userId}' on position '{positionId}' ({reason})", ct,
            (r, c) => r.EndAllForUserAndPositionAsync(userId, positionId, reason, c));

    public Task<int> EndAllForUserAsync(Guid userId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for user '{userId}' ({reason})", ct,
            (r, c) => r.EndAllForUserAsync(userId, reason, c));

    public Task<int> EndAllForPasskeyAsync(Guid credentialId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for passkey '{credentialId}' ({reason})", ct,
            (r, c) => r.EndAllForPasskeyAsync(credentialId, reason, c));

    public Task<int> EndAllForGrantAsync(Guid grantId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for grant '{grantId}' ({reason})", ct,
            (r, c) => r.EndAllForGrantAsync(grantId, reason, c));

    public Task<int> EndAllForActivationTokenAsync(Guid activationTokenId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for activation token '{activationTokenId}' ({reason})", ct,
            (r, c) => r.EndAllForActivationTokenAsync(activationTokenId, reason, c));

    public Task<int> EndAllForActivationTokenAndPositionAsync(Guid activationTokenId, Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default)
        => DeferOrRun($"end staffing sessions for activation token '{activationTokenId}' on position '{positionId}' ({reason})", ct,
            (r, c) => r.EndAllForActivationTokenAndPositionAsync(activationTokenId, positionId, reason, c));

    private Task<int> DeferOrRun(
        string what, CancellationToken ct,
        Func<StaffingRevoker, CancellationToken, Task<int>> call)
    {
        if (TenantApplyTransaction.Current is not { } apply)
            return call(inner, ct);
        apply.Defer(what, (sp, c) => call(sp.GetRequiredService<StaffingRevoker>(), c));
        return Task.FromResult(0);
    }
}
