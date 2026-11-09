using BuildingBlocks.EventDispatcher;
using Marten;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.PositionTerminals.Contracts.V1;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.Persistence.Tenancy;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Modgud.Infrastructure.PositionTerminals;

/// <summary>
/// <see cref="IStaffingRevoker"/> over the tenant-scoped session
/// factory: every session end runs in its OWN short Marten session, so the
/// revoker is safe to call from the middle of an endpoint that has staged
/// (uncommitted) changes — it never commits somebody else's work. The end
/// shape mirrors the lazy-expiry path in the staffing refresh: Ended event +
/// terminal-pointer clear + the <see cref="RevokeStaffingAuthorization"/> follow-up in ONE commit
/// (G-B4-02); the authorization is revoked right after as well, and the follow-up repeats that
/// until it held, so a failing revoke can no longer leave an ended session with a live grant.
/// A stream-version conflict (racing tap on the same terminal) is retried
/// once on a fresh session; idempotency comes from re-checking the session
/// status inside each attempt.
/// </summary>
public sealed class StaffingRevoker : IStaffingRevoker
{
    private readonly ITenantSessionFactory _sessionFactory;
    private readonly IOAuthGrantRevoker _grantRevoker;
    private readonly ISecurityAuditLog _securityAudit;
    private readonly DataEventDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IFollowUpOutbox _followUps;
    private readonly ILogger<StaffingRevoker> _logger;

    public StaffingRevoker(
        ITenantSessionFactory sessionFactory,
        IOAuthGrantRevoker grantRevoker,
        ISecurityAuditLog securityAudit,
        DataEventDispatcher dispatcher,
        IMessageBus bus,
        IFollowUpOutbox followUps,
        ILogger<StaffingRevoker> logger)
    {
        _sessionFactory = sessionFactory;
        _grantRevoker = grantRevoker;
        _securityAudit = securityAudit;
        _dispatcher = dispatcher;
        _bus = bus;
        _followUps = followUps;
        _logger = logger;
    }

    public Task<int> EndSessionAsync(Guid sessionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndByIdsAsync([sessionId], reason, ct);

    public Task<int> EndAllForTerminalAsync(Guid terminalId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.TerminalEnrollmentId == terminalId, reason, ct);

    public Task<int> EndAllForPositionAsync(Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.PositionPrincipalId == positionId, reason, ct);

    public Task<int> EndAllForUserAndPositionAsync(Guid userId, Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.ActivatedByUserId == userId && s.PositionPrincipalId == positionId, reason, ct);

    public Task<int> EndAllForUserAsync(Guid userId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.ActivatedByUserId == userId, reason, ct);

    public Task<int> EndAllForPasskeyAsync(Guid credentialId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.ActivatedByPasskeyCredentialId == credentialId, reason, ct);

    public Task<int> EndAllForGrantAsync(Guid grantId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.PositionGrantId == grantId, reason, ct);

    public Task<int> EndAllForActivationTokenAsync(Guid activationTokenId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.Evidence.ActivationTokenId == activationTokenId, reason, ct);

    public Task<int> EndAllForActivationTokenAndPositionAsync(Guid activationTokenId, Guid positionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.Evidence.ActivationTokenId == activationTokenId && s.PositionPrincipalId == positionId, reason, ct);

    private async Task<int> EndWhereAsync(
        System.Linq.Expressions.Expression<Func<StaffingSession, bool>> selector,
        StaffingSessionEndReason reason,
        CancellationToken ct)
    {
        List<Guid> ids;
        await using (var query = _sessionFactory.OpenQuerySession())
        {
            ids = (await query.Query<StaffingSession>()
                .Where(s => s.Status == StaffingSessionStatus.Active)
                .Where(selector)
                .ToListAsync(ct))
                .Select(s => s.Id)
                .ToList();
        }
        return await EndByIdsAsync(ids, reason, ct);
    }

    private async Task<int> EndByIdsAsync(IReadOnlyList<Guid> sessionIds, StaffingSessionEndReason reason, CancellationToken ct)
    {
        var ended = 0;
        foreach (var id in sessionIds)
        {
            if (await EndOneAsync(id, reason, retryOnConflict: true, ct)) ended++;
        }

        if (ended > 0)
        {
            _securityAudit.RecordTelemetry(new SecurityAuditRecord
            {
                EventType = AuditEvents.StaffingSessionEnded,
                RealmSlug = TenantContext.Current,
                ActorKind = AuditActorKind.System,
                OutcomeCode = AuditOutcomes.Completed,
                OperationCode = "staffing-end",
                ReasonCode = reason.ToString(),
                Count = ended,
            });
        }

        return ended;
    }

    private async Task<bool> EndOneAsync(Guid sessionId, StaffingSessionEndReason reason, bool retryOnConflict, CancellationToken ct)
    {
        await using var session = _sessionFactory.OpenSession();

        var staffing = await session.LoadAsync<StaffingSession>(sessionId, ct);
        if (staffing is null || staffing.Status != StaffingSessionStatus.Active)
            return false; // idempotent — already ended (possibly by the race we retried over)

        var now = DateTimeOffset.UtcNow;
        session.Events.Append(staffing.Id, new StaffingSessionEnded(staffing.Id, reason, now));

        // Clear the terminal's pointer only when it still belongs to this
        // session — a newer activation may already own the slot; the stream
        // version guards the check-then-append.
        var terminalStream = await session.Events.FetchForWriting<TerminalEnrollment>(staffing.TerminalEnrollmentId, ct);
        if (terminalStream.Aggregate?.ActiveStaffingSessionId == staffing.Id)
        {
            terminalStream.AppendOne(new TerminalStaffingSessionCleared(
                staffing.TerminalEnrollmentId, staffing.Id, now));
        }

        // G-B4-02 — the revoke is owed from the moment Ended is committed: write it in the same
        // commit, so a failure after the commit cannot lose it (an ended session is never
        // selected again by a repeated end, the sweep or the per-subject cascades).
        // A safety net for the immediate revoke after the commit, delivered once that finished.
        if (!string.IsNullOrEmpty(staffing.OAuthAuthorizationId))
            await _followUps.PublishSafetyNetAsync(session,
                new RevokeStaffingAuthorization(staffing.Id, staffing.OAuthAuthorizationId));

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException) when (retryOnConflict)
        {
            // A racing tap moved the terminal between fetch and save — the
            // whole unit of work rolled back. One retry on a fresh session.
            return await EndOneAsync(sessionId, reason, retryOnConflict: false, ct);
        }

        // The session's tokens die NOW (reference tokens + revoked authorization), not at the
        // next refresh. A failure here no longer fails the end: the committed follow-up repeats
        // the revoke until it held.
        try
        {
            // false also when the follow-up won the race on the same authorization (OpenIddict
            // reports the concurrency conflict as false) — not a warning on its own.
            if (!await _grantRevoker.RevokeAuthorizationByIdAsync(staffing.OAuthAuthorizationId, CancellationToken.None))
                _logger.LogInformation(
                    "Authorization {AuthorizationId} of ended staffing session {StaffingSessionId} was not revoked at once; the follow-up retries it",
                    staffing.OAuthAuthorizationId, staffing.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Revoking authorization {AuthorizationId} of ended staffing session {StaffingSessionId} failed; the follow-up retries it",
                staffing.OAuthAuthorizationId, staffing.Id);
        }

        _dispatcher.DispatchUpdatedEvent("StaffingSession", new
        {
            staffing.Id,
            Status = StaffingSessionStatus.Ended,
            EndReason = reason,
            TerminalId = staffing.TerminalEnrollmentId,
            PositionId = staffing.PositionPrincipalId,
        }, session.TenantId);

        // MG-FT-09 (§17) — the consumer notification: no person data, and
        // never the security boundary (the tokens above are already dead).
        await _bus.PublishAsync(new PositionStaffingSessionEnded(
            staffing.PositionPrincipalId, staffing.TerminalEnrollmentId,
            staffing.Id, reason, now));

        return true;
    }
}
