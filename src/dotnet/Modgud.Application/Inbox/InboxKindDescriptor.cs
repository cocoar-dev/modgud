namespace Modgud.Application.Inbox;

public enum InboxPersistenceMode
{
    /// <summary>Stays in the inbox until the user explicitly dismisses it.</summary>
    Persistent = 0,

    /// <summary>Persistent until read, then subject to retention policy (see <see cref="InboxRetentionSettings"/>).</summary>
    AutoExpire = 1,

    /// <summary>Live-push only (toast). Not persisted as a list item.</summary>
    Transient = 2,
}

public enum InboxDedupPolicy
{
    /// <summary>Every notify creates a fresh item.</summary>
    None = 0,

    /// <summary>If an open item with the same (Recipient, SourceType, SourceId) exists, replace it.</summary>
    ReplaceBySource = 1,
}

/// <summary>
/// Static metadata for an inbox kind — drives the bell/panel rendering and
/// dedup. Retention policy lives separately in <see cref="InboxRetentionSettings"/>
/// (admin-tunable per kind, with shapes tailored to each kind's lifecycle).
/// </summary>
public sealed record InboxKindDescriptor(
    InboxKind Kind,
    InboxPersistenceMode Persistence,
    bool Actionable,
    InboxDedupPolicy Dedup,
    InboxSeverity Severity,
    string Icon,
    string I18nPrefix);
