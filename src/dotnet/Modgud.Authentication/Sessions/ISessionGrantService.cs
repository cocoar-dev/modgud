using Marten;
using Modgud.Authentication.Events;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// ADR 0021 — maintains <see cref="SessionGrant"/> rows and appends the access
/// events on the user stream. The <c>Stage*</c> members add to the caller's unit of
/// work and never save: the session delete, the grant deletes and the event commit
/// together or not at all.
/// </summary>
public interface ISessionGrantService
{
    /// <summary>Upsert on every access-token mint for a (session, client) pair; appends
    /// <see cref="UserAccessGrantedEvent"/> the first time. Saves its own unit of work.</summary>
    Task RecordIssuanceAsync(
        Guid sessionId,
        Guid userId,
        string clientId,
        string applicationId,
        AccessSessionKind kind,
        string issuer,
        CancellationToken ct = default);

    /// <summary>Stages the deletion of the session's grants and, when the session had
    /// any relying party, appends a <see cref="AccessEndScope.Session"/> event carrying
    /// them. Returns the number of grants.</summary>
    Task<int> StageSessionEndAsync(
        IDocumentSession session,
        Guid userId,
        Guid sessionId,
        string reason,
        string? initiatingClientId,
        CancellationToken ct = default);

    /// <summary>Stages the deletion of every grant of the user and appends one
    /// <see cref="AccessEndScope.User"/> event carrying every relying party that held
    /// tokens for the user. Always appends — a user-level end is a fact even without
    /// relying parties. Returns the number of grants.</summary>
    Task<int> StageUserEndAsync(
        IDocumentSession session,
        Guid userId,
        string reason,
        CancellationToken ct = default);

    /// <summary>Hard-deletes grants whose session no longer exists (defence against a
    /// session removed outside the session services). Returns the number deleted.</summary>
    Task<int> SweepOrphansAsync(CancellationToken ct = default);
}
