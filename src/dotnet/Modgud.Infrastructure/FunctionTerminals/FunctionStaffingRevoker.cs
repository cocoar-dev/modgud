using BuildingBlocks.EventDispatcher;
using Marten;
using Modgud.Domain.FunctionTerminals;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.FunctionTerminals;

/// <summary>
/// <see cref="IFunctionStaffingRevoker"/> over the tenant-scoped session
/// factory: every session end runs in its OWN short Marten session, so the
/// revoker is safe to call from the middle of an endpoint that has staged
/// (uncommitted) changes — it never commits somebody else's work. The end
/// shape mirrors the lazy-expiry path in the staffing refresh: Ended event +
/// terminal-pointer clear in ONE commit, authorization revoke right after.
/// A stream-version conflict (racing tap on the same terminal) is retried
/// once on a fresh session; idempotency comes from re-checking the session
/// status inside each attempt.
/// </summary>
public sealed class FunctionStaffingRevoker : IFunctionStaffingRevoker
{
    private readonly ITenantSessionFactory _sessionFactory;
    private readonly IOAuthGrantRevoker _grantRevoker;
    private readonly ISecurityAuditLog _securityAudit;
    private readonly DataEventDispatcher _dispatcher;

    public FunctionStaffingRevoker(
        ITenantSessionFactory sessionFactory,
        IOAuthGrantRevoker grantRevoker,
        ISecurityAuditLog securityAudit,
        DataEventDispatcher dispatcher)
    {
        _sessionFactory = sessionFactory;
        _grantRevoker = grantRevoker;
        _securityAudit = securityAudit;
        _dispatcher = dispatcher;
    }

    public Task<int> EndSessionAsync(Guid sessionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndByIdsAsync([sessionId], reason, ct);

    public Task<int> EndAllForTerminalAsync(Guid terminalId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.TerminalEnrollmentId == terminalId, reason, ct);

    public Task<int> EndAllForFunctionAsync(Guid functionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.FunctionPrincipalId == functionId, reason, ct);

    public Task<int> EndAllForUserAndFunctionAsync(Guid userId, Guid functionId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.ActivatedByUserId == userId && s.FunctionPrincipalId == functionId, reason, ct);

    public Task<int> EndAllForUserAsync(Guid userId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.ActivatedByUserId == userId, reason, ct);

    public Task<int> EndAllForPasskeyAsync(Guid credentialId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.ActivatedByPasskeyCredentialId == credentialId, reason, ct);

    public Task<int> EndAllForGrantAsync(Guid grantId, StaffingSessionEndReason reason, CancellationToken ct = default) =>
        EndWhereAsync(s => s.FunctionActivationGrantId == grantId, reason, ct);

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

        // The session's tokens die NOW (reference tokens + revoked
        // authorization), not at the next refresh.
        await _grantRevoker.RevokeAuthorizationByIdAsync(staffing.OAuthAuthorizationId, CancellationToken.None);

        _dispatcher.DispatchUpdatedEvent("StaffingSession", new
        {
            staffing.Id,
            Status = StaffingSessionStatus.Ended,
            EndReason = reason,
            TerminalId = staffing.TerminalEnrollmentId,
            FunctionId = staffing.FunctionPrincipalId,
        }, session.TenantId);

        return true;
    }
}
