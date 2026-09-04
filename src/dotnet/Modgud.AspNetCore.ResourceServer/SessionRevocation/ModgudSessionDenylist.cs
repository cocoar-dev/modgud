using System.Collections.Concurrent;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>
/// Read side of session revocation: the ended session ids this resource server knows
/// about. Registered as a singleton when <see cref="ModgudSessionRevocationOptions.Enabled"/>
/// is on; consult it for health endpoints or diagnostics.
/// </summary>
public interface IModgudSessionDenylist
{
    /// <summary><c>true</c> when the session named by the <c>sid</c> claim has ended.</summary>
    bool IsRevoked(string sessionId);

    /// <summary>Number of session ids currently on the denylist.</summary>
    int Count { get; }

    /// <summary>When the feed was last read successfully; <c>null</c> until the first
    /// successful read. A stale value means the worker is retrying and revocations
    /// may be missed (fail-open).</summary>
    DateTimeOffset? LastSyncedAt { get; }
}

internal sealed class ModgudSessionDenylist(TimeProvider clock) : IModgudSessionDenylist
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);
    private long _lastSyncedTicks;

    public int Count => _expiries.Count;

    public DateTimeOffset? LastSyncedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSyncedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public bool IsRevoked(string sessionId)
    {
        if (!_expiries.TryGetValue(sessionId, out var expiry)) return false;
        if (expiry > clock.GetUtcNow()) return true;
        _expiries.TryRemove(sessionId, out _);
        return false;
    }

    public void Revoke(string sessionId, DateTimeOffset until) =>
        _expiries.AddOrUpdate(sessionId, until, (_, existing) => existing > until ? existing : until);

    public void MarkSynced() => Interlocked.Exchange(ref _lastSyncedTicks, clock.GetUtcNow().UtcTicks);

    public int Prune()
    {
        var now = clock.GetUtcNow();
        var removed = 0;
        foreach (var (sid, expiry) in _expiries)
        {
            if (expiry <= now && _expiries.TryRemove(sid, out _)) removed++;
        }
        return removed;
    }
}
