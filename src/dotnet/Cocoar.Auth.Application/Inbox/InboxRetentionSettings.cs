namespace Cocoar.Auth.Application.Inbox;

/// <summary>
/// App-wide retention policy for the inbox, structured per domain. Each
/// kind has its own typed section because the lifecycles differ —
/// change-request feedback is time-based, admin-pending items are
/// event-driven (live until dismissed, then a clean-up window for the
/// dismissed audit row).
///
/// Singleton document (fixed <see cref="SingletonId"/>); admins edit it
/// via <c>/admin/inbox-settings</c>. The <c>inbox-retention</c> Quartz
/// job reads from here and applies per-kind logic.
///
/// Persistence binding (DocumentAlias + Identity) lives in
/// <c>Cocoar.Auth.Infrastructure.Persistence.Marten.Configuration.MartenConfiguration</c>
/// so Application stays free of Marten attributes.
/// </summary>
public class InboxRetentionSettings
{
    /// <summary>Stable id so we always hit the same document.</summary>
    public static readonly Guid SingletonId = new("0a0a0a0a-1111-2222-3333-444444444444");

    public Guid Id { get; set; } = SingletonId;

    public AdminChangeRequestRetention AdminChangeRequest { get; set; } = new();
    public ChangeRequestFeedbackRetention ChangeRequestFeedback { get; set; } = new();
    public ScheduledJobFeedbackRetention ScheduledJobFeedback { get; set; } = new();

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Event-driven retention — open items live until the underlying change-request is
/// approved/rejected (which auto-dismisses the inbox item via the explicit dismiss
/// chain). The only retention knob is how long the dismissed row stays around for
/// audit before hard-delete.
/// </summary>
public class AdminChangeRequestRetention
{
    /// <summary>Hard-delete items this many days after they were dismissed. <c>null</c> = never.</summary>
    public int? HardDeleteDaysAfterDismissed { get; set; } = 30;
}

/// <summary>
/// FYI feedback to the requester (approved / rejected). Standard time-based shape.
/// </summary>
public class ChangeRequestFeedbackRetention
{
    /// <summary>Dismiss UNREAD items older than this many days. <c>null</c> = never.</summary>
    public int? MaxUnreadDays { get; set; } = 60;

    /// <summary>Dismiss READ items this many days after they were read. <c>null</c> = never.</summary>
    public int? AutoExpireDaysAfterRead { get; set; } = 30;
}

/// <summary>
/// Operational feedback for the scheduled-jobs subsystem (manual-trigger
/// completions for the triggering user, failures for admins). Shorter defaults
/// than user feedback because operational signals get noisier and admins
/// don't need ancient ones once the underlying issue is fixed.
/// </summary>
public class ScheduledJobFeedbackRetention
{
    /// <summary>Dismiss UNREAD items older than this many days. <c>null</c> = never.</summary>
    public int? MaxUnreadDays { get; set; } = 30;

    /// <summary>Dismiss READ items this many days after they were read. <c>null</c> = never.</summary>
    public int? AutoExpireDaysAfterRead { get; set; } = 14;
}
