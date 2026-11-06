using System.Collections.Concurrent;

namespace Modgud.AspNetCore.ResourceServer.Dpop;

/// <summary>
/// Remembers the DPoP proofs a resource server has accepted, so a recorded request (access
/// token plus its proof) cannot be replayed within the proof's freshness window (RFC 9449
/// §11.1). The SDK registers <see cref="InMemoryDpopReplayCache"/> by default; register your
/// own implementation before calling <c>AddModgudResourceServer</c> to share the record across
/// several instances (e.g. backed by a distributed cache).
/// </summary>
public interface IDpopReplayCache
{
    /// <summary>
    /// Records the proof identified by <paramref name="jkt"/> + <paramref name="jti"/> until
    /// <paramref name="notAfter"/>. Returns <c>false</c> when that proof was already recorded
    /// and has not expired yet — the request is a replay.
    /// </summary>
    bool TryUse(string jkt, string jti, DateTimeOffset notAfter, DateTimeOffset now);
}

/// <summary>
/// Default <see cref="IDpopReplayCache"/>: per process, in memory. Entries live only for the
/// proof's freshness window (about 65 seconds), so the set stays small. Behind a load balancer
/// without session affinity a replay may still reach another instance — use a shared cache
/// there.
/// </summary>
public sealed class InMemoryDpopReplayCache : IDpopReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private long _calls;

    public bool TryUse(string jkt, string jti, DateTimeOffset notAfter, DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _calls) % 256 == 0) Prune(now);

        var key = jkt + "\n" + jti;
        while (true)
        {
            if (_seen.TryAdd(key, notAfter)) return true;
            if (!_seen.TryGetValue(key, out var until)) continue;
            if (until > now) return false;
            // The earlier record has expired: the same identifiers may be used again.
            if (_seen.TryUpdate(key, notAfter, until)) return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, until) in _seen)
            if (until <= now) _seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
    }
}
