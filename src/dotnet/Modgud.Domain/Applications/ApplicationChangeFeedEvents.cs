namespace Modgud.Domain.Applications;

/// <summary>
/// Durable source signal for a change-feed policy change. The settings
/// document remains the query model; this event lets the high-water-anchored
/// feed subscription observe enable/disable and retention changes in the same
/// ordered source as all event-sourced domain changes.
/// </summary>
public sealed record ApplicationChangeFeedConfiguredEvent(
    Guid ApplicationId,
    bool Enabled,
    int MinimumRetentionAgeDays,
    int MinimumEventCount,
    DateTimeOffset ChangedAt);
