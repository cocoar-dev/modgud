namespace Cocoar.Auth.Application.Inbox;

/// <summary>
/// Static registry of all known inbox kinds. To add a new kind, see the
/// step list on <see cref="InboxKind"/>. Retention policy lives in
/// <see cref="InboxRetentionSettings"/>; new kinds that need retention
/// behaviour must extend that doc too.
/// </summary>
public static class InboxKindRegistry
{
    private static readonly Dictionary<InboxKind, InboxKindDescriptor> _byKind = new()
    {
        [InboxKind.AdminChangeRequestSubmitted] = new InboxKindDescriptor(
            Kind: InboxKind.AdminChangeRequestSubmitted,
            Persistence: InboxPersistenceMode.Persistent,
            Actionable: true,
            // Re-submits / merges by the same user collapse onto one bell entry
            // per request. SourceId carries the change-request id.
            Dedup: InboxDedupPolicy.ReplaceBySource,
            Severity: InboxSeverity.Info,
            Icon: "clipboard-list",
            I18nPrefix: "inbox.kinds.adminChangeRequestSubmitted."),

        [InboxKind.ChangeRequestApproved] = new InboxKindDescriptor(
            Kind: InboxKind.ChangeRequestApproved,
            Persistence: InboxPersistenceMode.AutoExpire,
            Actionable: false,
            Dedup: InboxDedupPolicy.None,
            Severity: InboxSeverity.Success,
            Icon: "circle-check",
            I18nPrefix: "inbox.kinds.changeRequestApproved."),

        [InboxKind.ChangeRequestRejected] = new InboxKindDescriptor(
            Kind: InboxKind.ChangeRequestRejected,
            Persistence: InboxPersistenceMode.AutoExpire,
            Actionable: false,
            Dedup: InboxDedupPolicy.None,
            Severity: InboxSeverity.Warning,
            Icon: "circle-x",
            I18nPrefix: "inbox.kinds.changeRequestRejected."),

        [InboxKind.ScheduledJobFailed] = new InboxKindDescriptor(
            Kind: InboxKind.ScheduledJobFailed,
            Persistence: InboxPersistenceMode.Persistent,
            Actionable: false,
            // Repeated failures of the same job collapse onto one bell entry —
            // admins fix the root cause, not the count. SourceId carries the
            // hash of the job key (Guid form so the projection field type matches).
            Dedup: InboxDedupPolicy.ReplaceBySource,
            Severity: InboxSeverity.Critical,
            Icon: "alert-triangle",
            I18nPrefix: "inbox.kinds.scheduledJobFailed."),

        [InboxKind.ManualJobCompleted] = new InboxKindDescriptor(
            Kind: InboxKind.ManualJobCompleted,
            Persistence: InboxPersistenceMode.AutoExpire,
            Actionable: false,
            Dedup: InboxDedupPolicy.None,
            Severity: InboxSeverity.Success,
            Icon: "play-circle",
            I18nPrefix: "inbox.kinds.manualJobCompleted."),
    };

    public static InboxKindDescriptor Get(InboxKind kind)
    {
        if (!_byKind.TryGetValue(kind, out var d))
            throw new InvalidOperationException($"InboxKind {kind} is not registered. Add an entry to InboxKindRegistry.");
        return d;
    }

    public static IReadOnlyCollection<InboxKindDescriptor> All => _byKind.Values;
}
