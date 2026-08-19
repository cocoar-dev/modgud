namespace Modgud.Infrastructure.ChangeFeed;

/// <summary>Per-Application durable progress and retention state.</summary>
public sealed class AppChangeFeedState
{
    public Guid Id { get; set; }
    public bool Enabled { get; set; }
    public int Generation { get; set; }
    public string ScopeVersion { get; set; } = string.Empty;
    public long LastProcessedSequence { get; set; }
    public long RetentionFloorSequence { get; set; }
    public int RetentionFloorOrdinal { get; set; } = -1;
    public int MinimumRetentionAgeDays { get; set; } = 7;
    public int MinimumEventCount { get; set; } = 1_000;
    public DateTimeOffset? LastCompactedAt { get; set; }
}

/// <summary>
/// Last public representation emitted for one entity in one Application. This
/// is feed-owned projection state, not a second source of business truth.
/// </summary>
public sealed class AppChangeFeedEntityState
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public string EntityKind { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
}

/// <summary>
/// Short-lived integration envelope. The source event store remains the
/// durable business history; these rows exist only to make consumer resume
/// possible for the configured retention window.
/// </summary>
public sealed class AppChangeFeedEntry
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public int Generation { get; set; }
    public long SourceSequence { get; set; }
    public int Ordinal { get; set; }
    public string ScopeVersion { get; set; } = string.Empty;
    public Guid? SourceEventId { get; set; }
    public DateTimeOffset OriginatedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string ChangeKind { get; set; } = string.Empty;
    public string? EntityKind { get; set; }
    public Guid? EntityId { get; set; }
    public string? PayloadJson { get; set; }
    public string? Reason { get; set; }
}
