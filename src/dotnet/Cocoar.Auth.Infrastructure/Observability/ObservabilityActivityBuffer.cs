using System.Collections.Concurrent;

namespace Cocoar.Auth.Infrastructure.Observability;

/// <summary>
/// In-memory circular buffer for the in-app live observability view
/// (Phase 5). Captures the same events that <see cref="CocoarAuthMeters"/>
/// emits to OpenTelemetry — but as discrete records rather than aggregated
/// counters, so the admin UI can show an activity feed + bucketed
/// sparklines without standing up Prometheus.
///
/// <para>Fixed-size ring buffer; old events overwrite the oldest slot
/// once capacity is reached. Default 1000 events keeps memory bounded
/// (~100 KB at typical tag payload size) and is sufficient for a
/// rolling 15min window at modest production rates.</para>
///
/// <para>Multi-instance note: this buffer is local to the process.
/// Phase 5 deliberately doesn't replicate across instances — see the
/// HA / Multi-Instance dev-note for the broader Redis/SignalR-backplane
/// trade-off. Single-instance is the supported deployment shape today.</para>
/// </summary>
public sealed class ObservabilityActivityBuffer
{
    public const int DefaultCapacity = 1000;

    private readonly ConcurrentQueue<ObservabilityEvent> _events = new();
    private readonly int _capacity;

    public ObservabilityActivityBuffer(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
    }

    public void Record(string eventType, string realm, IReadOnlyDictionary<string, string>? tags = null)
    {
        var evt = new ObservabilityEvent(
            DateTimeOffset.UtcNow,
            eventType,
            realm,
            tags ?? EmptyTags);
        _events.Enqueue(evt);

        // Cheap trim — never block; over-shoot by a few entries under
        // burst contention is fine.
        while (_events.Count > _capacity && _events.TryDequeue(out _)) { }
    }

    /// <summary>Most-recent first.</summary>
    public IReadOnlyList<ObservabilityEvent> GetRecent(int limit)
    {
        var snapshot = _events.ToArray();
        var take = Math.Min(limit, snapshot.Length);
        var result = new ObservabilityEvent[take];
        // Reverse-fill: latest events sit at the END of the queue.
        for (var i = 0; i < take; i++)
            result[i] = snapshot[snapshot.Length - 1 - i];
        return result;
    }

    /// <summary>Events newer than <paramref name="cutoff"/>, in chronological order.</summary>
    public IReadOnlyList<ObservabilityEvent> GetSince(DateTimeOffset cutoff, string? realmFilter = null)
    {
        var snapshot = _events.ToArray();
        var filtered = new List<ObservabilityEvent>();
        foreach (var e in snapshot)
        {
            if (e.Timestamp < cutoff) continue;
            if (realmFilter is not null && !string.Equals(e.Realm, realmFilter, StringComparison.Ordinal)) continue;
            filtered.Add(e);
        }
        return filtered;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>();
}

public record ObservabilityEvent(
    DateTimeOffset Timestamp,
    string EventType,
    string Realm,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Event-type constants shared between the buffer and the API surface.
/// Mirrors the meter-name segments so admin UI filters can correlate
/// with /metrics labels one-to-one.
/// </summary>
public static class ObservabilityEventTypes
{
    public const string Login = "login";
    public const string TokenMinted = "token.minted";
    public const string TokenRefreshRejected = "token.refresh.rejected";
    public const string TwoFactorBlocked = "two_factor.blocked";
    public const string DcrRegistration = "dcr.registration";
    public const string DcrRateLimitHit = "dcr.rate_limit.hit";
    public const string RealmProvisioned = "realm.provisioned";
    public const string GdprRequest = "gdpr.request";
}
