using System.Security.Cryptography;
using System.Text;
using Cocoar.Auth.Application.Inbox;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Infrastructure.Scheduling;

namespace Cocoar.Auth.Api.Features.Inbox;

/// <summary>
/// Bridges Phase-1A's <see cref="JobRunListener"/> to Phase-2A's
/// <see cref="IInboxNotifier"/>. Two cases:
///
/// <list type="bullet">
///   <item><description><b>Failed run</b> (regardless of trigger source) — notify all admin
///     users (<see cref="IAdminNotifier.GetAdminRecipientUserIdsAsync"/>) with
///     <see cref="InboxKind.ScheduledJobFailed"/>. Dedup-by-source keys on the
///     job-key-derived <see cref="Guid"/> so repeated failures of the same job
///     collapse onto one bell entry per admin — they fix the root cause once.</description></item>
///   <item><description><b>Manual trigger completion</b> (success or fail, but only when
///     the trigger captured a user-id) — notify the triggering user with
///     <see cref="InboxKind.ManualJobCompleted"/>. Auto-scheduled runs intentionally
///     don't notify; operators don't need a bell ping every cron tick.</description></item>
/// </list>
/// </summary>
public class JobRunNotifier(
    IInboxNotifier inbox,
    IAdminNotifier adminNotifier) : IJobRunNotifier
{
    private const string JobSourceType = "scheduled-job";

    public async Task NotifyAsync(JobRunHistoryEntry entry, CancellationToken ct = default)
    {
        // ── Failure → notify admins (collapsed per job key) ──────────────
        if (!entry.Success)
        {
            var adminIds = await adminNotifier.GetAdminRecipientUserIdsAsync(ct);
            if (adminIds.Count > 0)
            {
                await inbox.NotifyAsync(
                    kind: InboxKind.ScheduledJobFailed,
                    recipients: adminIds,
                    titleKey: "inbox.kinds.scheduledJobFailed.title",
                    bodyKey: "inbox.kinds.scheduledJobFailed.body",
                    parameters: new
                    {
                        jobKey = entry.JobKey,
                        error = entry.ErrorMessage,
                        startedAt = entry.StartedAt,
                    },
                    link: $"/admin/scheduled-jobs#{entry.JobKey}",
                    sourceType: JobSourceType,
                    sourceId: JobKeyToSourceId(entry.JobKey),
                    ct: ct);
            }
        }

        // ── Manual trigger completion → notify the triggering user ───────
        if (entry.ManualTrigger && entry.TriggeredByUserId is Guid userId && userId != Guid.Empty)
        {
            await inbox.NotifyAsync(
                kind: InboxKind.ManualJobCompleted,
                recipients: [userId],
                titleKey: entry.Success
                    ? "inbox.kinds.manualJobCompleted.titleSuccess"
                    : "inbox.kinds.manualJobCompleted.titleFailed",
                bodyKey: "inbox.kinds.manualJobCompleted.body",
                parameters: new
                {
                    jobKey = entry.JobKey,
                    success = entry.Success,
                    durationMs = entry.DurationMs,
                    resultSummary = entry.ResultSummary,
                    errorMessage = entry.ErrorMessage,
                },
                link: $"/admin/scheduled-jobs#{entry.JobKey}",
                sourceType: JobSourceType,
                sourceId: JobKeyToSourceId(entry.JobKey),
                ct: ct);
        }
    }

    /// <summary>
    /// Stable Guid derived from the job key — lets the ReplaceBySource dedup
    /// on ScheduledJobFailed collapse multiple failures of the same job onto
    /// one bell entry per admin without us needing to store an explicit
    /// per-job source-id Guid anywhere. SHA-256 (not SHA-1) so the SAST
    /// CA5350 gate stays clean; we use the first 16 bytes — collisions on
    /// truncated SHA-256 are astronomically unlikely for the small set of
    /// job keys we register.
    /// </summary>
    private static Guid JobKeyToSourceId(string jobKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(jobKey));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
