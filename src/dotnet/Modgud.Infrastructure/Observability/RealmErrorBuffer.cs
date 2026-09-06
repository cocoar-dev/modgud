using System.Collections.Concurrent;

namespace Modgud.Infrastructure.Observability;

/// <summary>
/// In-memory live error feed for the in-app observability view
/// (logging/audit redesign Phase 5, §B.3). Captures recent operational
/// error records so a realm-admin can live-tail "what is erroring on my
/// realm" without standing up OpenObserve.
///
/// <para><b>Per-realm-bounded buffers — deliberately NOT a single global
/// ring.</b> The sibling <see cref="ObservabilityActivityBuffer"/> is one
/// global ring with query-time realm filtering, where a loud realm provably
/// evicts a quiet realm's events before its admin sees them. This buffer keeps
/// an <i>independently-capped ring per realm</i> (keyed by realm slug), so a
/// noisy realm can only evict <i>its own</i> oldest entries — a quiet realm's
/// error visibility can never be starved. Each realm ring evicts its own
/// oldest; there is no retention job (§B.3).</para>
///
/// <para>Memory is bounded by <c>realms × capacityPerRealm</c>; realm count is
/// bounded by the tenant count and each ring is fixed-size, so the total stays
/// small. Rings are created lazily on first record for a realm.</para>
///
/// <para><b>This feed does NOT pass through the OTel collector redaction.</b>
/// Like the streamless security store, the call-site PII belt
/// (<c>LogPiiMasking</c> + the Phase-4 source-belt that logs <c>user.Id</c>
/// rather than usernames) plus per-realm read scoping are the only PII
/// controls here. Entries are rendered+truncated at capture by
/// <c>ErrorFeedSink</c>.</para>
///
/// <para>Multi-instance note: this buffer is local to the process, so with
/// two instances an admin sees the errors of the node their connection is
/// pinned to. Persisting it is the open item of ADR 0022, increment 2.</para>
/// </summary>
public sealed class RealmErrorBuffer
{
    public const int DefaultCapacityPerRealm = 100;

    private readonly ConcurrentDictionary<string, RealmRing> _rings =
        new(StringComparer.Ordinal);
    private readonly int _capacityPerRealm;

    public RealmErrorBuffer(int capacityPerRealm = DefaultCapacityPerRealm)
    {
        _capacityPerRealm = capacityPerRealm < 1 ? DefaultCapacityPerRealm : capacityPerRealm;
    }

    /// <summary>
    /// Fired after every <see cref="Record"/> so live observers (the SignalARR
    /// <c>ObservabilityHub.LogsSubscribe</c>) can push to subscribed clients
    /// without polling. Handlers are invoked synchronously on the recording
    /// (log-emit) thread — keep them cheap. Handler exceptions are swallowed so
    /// a buggy subscriber can't break logging for everyone else.
    /// </summary>
    public event Action<ErrorLogEntry>? EntryRecorded;

    public void Record(ErrorLogEntry entry)
    {
        var ring = _rings.GetOrAdd(entry.Realm, _ => new RealmRing(_capacityPerRealm));
        ring.Add(entry);

        var handler = EntryRecorded;
        if (handler is null) return;
        foreach (var single in handler.GetInvocationList())
        {
            try { ((Action<ErrorLogEntry>)single)(entry); }
            catch { /* swallow — never let a subscriber break logging */ }
        }
    }

    /// <summary>Most-recent first, for the given realm only. Unknown realm → empty.</summary>
    public IReadOnlyList<ErrorLogEntry> GetRecent(string realm, int limit)
        => _rings.TryGetValue(realm, out var ring)
            ? ring.Snapshot(limit)
            : Array.Empty<ErrorLogEntry>();

    /// <summary>A single realm's fixed-size FIFO ring. Independently capped.</summary>
    private sealed class RealmRing
    {
        private readonly object _gate = new();
        private readonly Queue<ErrorLogEntry> _items;
        private readonly int _capacity;

        public RealmRing(int capacity)
        {
            _capacity = capacity;
            _items = new Queue<ErrorLogEntry>(capacity);
        }

        public void Add(ErrorLogEntry entry)
        {
            lock (_gate)
            {
                _items.Enqueue(entry);
                while (_items.Count > _capacity) _items.Dequeue();
            }
        }

        public IReadOnlyList<ErrorLogEntry> Snapshot(int limit)
        {
            lock (_gate)
            {
                var arr = _items.ToArray(); // oldest..newest
                var take = Math.Min(limit, arr.Length);
                var result = new ErrorLogEntry[take];
                // Reverse-fill: newest entries sit at the END of the queue.
                for (var i = 0; i < take; i++)
                    result[i] = arr[arr.Length - 1 - i];
                return result;
            }
        }
    }
}

/// <summary>
/// One captured operational error, already rendered and truncated at the sink.
/// No raw <c>LogEvent</c> / exception object is retained — only display-safe
/// strings — so the buffer holds no live references and a bounded footprint.
/// </summary>
public record ErrorLogEntry(
    DateTimeOffset Timestamp,
    string Realm,
    string Level,
    string Message,
    string? Exception,
    string? SourceContext,
    string? TraceId);
