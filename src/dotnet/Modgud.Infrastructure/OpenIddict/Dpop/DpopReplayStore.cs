using Marten;
using Modgud.Domain.OAuth.Storage;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Records DPoP proof <c>jti</c>s so a captured proof can't be replayed within
/// its acceptance window (RFC 9449 §11.1). Backed by the tenant-scoped Marten
/// session, so the check is shared across every app instance pointed at the same
/// realm database (multi-instance-safe), unlike a process-local cache.
/// </summary>
public interface IDpopReplayStore
{
    /// <summary>
    /// Atomically record first use of <paramref name="jti"/>. Returns <c>true</c>
    /// if it was newly recorded, <c>false</c> if it had already been seen (a
    /// replay) — or if the store couldn't confirm the write, in which case we
    /// fail closed and treat the proof as spent.
    /// </summary>
    Task<bool> TryRecordAsync(string jti, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct);
}

internal sealed class MartenDpopReplayStore : IDpopReplayStore
{
    private readonly IDocumentSession _session;

    public MartenDpopReplayStore(IDocumentSession session) => _session = session;

    public async Task<bool> TryRecordAsync(string jti, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
    {
        // Fast path: already recorded in this realm's DB → replay.
        var existing = await _session.LoadAsync<DpopReplayEntry>(jti, ct);
        if (existing is not null)
            return false;

        // Opportunistic prune of entries that can no longer be accepted anyway —
        // keeps the table bounded without a separate background job. Cheap: the
        // ExpiresAt index turns this into a small range delete, usually 0 rows.
        _session.DeleteWhere<DpopReplayEntry>(x => x.ExpiresAt < now);

        // Insert (not upsert) so a concurrent request with the same jti loses the
        // primary-key race and is rejected as a replay.
        _session.Insert(new DpopReplayEntry { Id = jti, ExpiresAt = expiresAt });
        try
        {
            await _session.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception)
        {
            // Lost the PK race (another instance recorded this jti first), or the
            // write failed. Either way, fail closed: don't hand out a token bound
            // to a proof we couldn't prove was fresh.
            return false;
        }
    }
}
