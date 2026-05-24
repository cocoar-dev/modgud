namespace Cocoar.Auth.Application.Inbox;

/// <summary>
/// All inbox-notification kinds known to Cocoar.Auth. To add a new kind:
///   1. Append the enum value below (NEVER reuse an old number — Marten
///      events serialise it as an int).
///   2. Add a matching <see cref="InboxKindDescriptor"/> entry to
///      <see cref="InboxKindRegistry"/>.
///   3. Wire a notify call-site that resolves recipients + invokes
///      <c>IInboxNotifier.NotifyAsync(kind, ...)</c>.
///   4. If retention behaviour differs from the existing sections, extend
///      <see cref="InboxRetentionSettings"/> with a new typed section and
///      handle it in <c>InboxRetentionService.ExecuteAsync</c>.
/// </summary>
public enum InboxKind
{
    /// <summary>
    /// A user submitted a change-request that reached <c>AdminApprovalPending</c>.
    /// Sent to every admin recipient. Auto-dismissed for the *other* admins once
    /// one admin approves/rejects the request, so the bell decrements naturally.
    /// </summary>
    AdminChangeRequestSubmitted = 1,

    /// <summary>An admin approved a user's change-request. Sent to the requester.</summary>
    ChangeRequestApproved = 2,

    /// <summary>An admin rejected a user's change-request. Sent to the requester.</summary>
    ChangeRequestRejected = 3,

    /// <summary>
    /// A scheduled Quartz job failed. Sent to admin recipients. Replace-by-source
    /// dedup keyed on the job key collapses repeated failures of the same job onto
    /// one bell entry — admins fix the root cause, not the count.
    /// </summary>
    ScheduledJobFailed = 4,

    /// <summary>
    /// A scheduled Quartz job that was manually triggered finished. Sent to the
    /// user who triggered it (captured at trigger-time on the run history entry).
    /// Auto-scheduled runs don't notify — only the manual triggers do.
    /// </summary>
    ManualJobCompleted = 5,
}
