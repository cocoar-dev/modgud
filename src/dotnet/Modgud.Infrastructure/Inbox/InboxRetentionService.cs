using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Application.Inbox;
using Modgud.Application.Inbox.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Infrastructure.Inbox;

/// <inheritdoc />
public class InboxRetentionService(
    IDocumentSession session,
    ILogger<InboxRetentionService> logger) : IInboxRetentionService
{
    public async Task<InboxRetentionResult> ExecuteAsync(CancellationToken ct = default)
    {
        // Settings live as a singleton Marten doc — load-or-default so a fresh
        // install with no admin click yet still has sensible behaviour.
        var settings = await session.LoadAsync<InboxRetentionSettings>(InboxRetentionSettings.SingletonId, ct)
            ?? new InboxRetentionSettings();

        var counts = new Dictionary<string, int>();
        var total = 0;
        var now = DateTime.UtcNow;

        // ── 1. Change-request feedback (Approved / Rejected) — time-based ─────
        total += await TimeBasedDismissAsync(
            kind: InboxKind.ChangeRequestApproved,
            maxUnreadDays: settings.ChangeRequestFeedback.MaxUnreadDays,
            maxReadDays: settings.ChangeRequestFeedback.AutoExpireDaysAfterRead,
            now, counts, ct);
        total += await TimeBasedDismissAsync(
            kind: InboxKind.ChangeRequestRejected,
            maxUnreadDays: settings.ChangeRequestFeedback.MaxUnreadDays,
            maxReadDays: settings.ChangeRequestFeedback.AutoExpireDaysAfterRead,
            now, counts, ct);

        // ── 2. Scheduled-job feedback (ManualJobCompleted) — time-based ──────
        // ScheduledJobFailed is intentionally NOT auto-dismissed here. Failures
        // are Persistent + ReplaceBySource (one open entry per job key); they
        // disappear when the operator dismisses, or when a successful run of
        // the same job logically replaces them (planned follow-up — not in v1).
        total += await TimeBasedDismissAsync(
            kind: InboxKind.ManualJobCompleted,
            maxUnreadDays: settings.ScheduledJobFeedback.MaxUnreadDays,
            maxReadDays: settings.ScheduledJobFeedback.AutoExpireDaysAfterRead,
            now, counts, ct);

        // ── 3. Admin change-request items — hard-delete N days after dismiss ─
        // Open items are NEVER touched here — they get dismissed by the explicit
        // approve/reject/withdraw chain, not by retention. Once dismissed, they
        // sit as audit rows until the cleanup window passes.
        if (settings.AdminChangeRequest.HardDeleteDaysAfterDismissed is int hardDeleteDays && hardDeleteDays > 0)
        {
            var cutoff = CutoffFromNowMinusDays(hardDeleteDays);
            var staleDismissed = await session.Query<InboxItemView>()
                .Where(i => i.Kind == InboxKind.AdminChangeRequestSubmitted
                         && i.DismissedAt != null
                         && i.DismissedAt < cutoff)
                .Select(i => i.Id)
                .ToListAsync(ct);

            foreach (var id in staleDismissed)
            {
                // Wipe the stream + projection doc.
                session.Events.ArchiveStream(id);
                session.Delete<InboxItemView>(id);
            }

            if (staleDismissed.Count > 0)
            {
                counts["AdminChangeRequestSubmitted.hard-deleted"] = staleDismissed.Count;
                total += staleDismissed.Count;
            }
        }

        if (total > 0)
        {
            await session.SaveChangesAsync(ct);
            logger.LogInformation(
                "[Inbox:Retention] Touched {Total} items across {BucketCount} buckets",
                total, counts.Count);
        }

        return new InboxRetentionResult(total, counts);
    }

    /// <summary>
    /// Soft-dismiss for the standard "two-number" kinds (Unread &gt; N → dismiss,
    /// Read &gt; N → dismiss). Returns the number of items touched; populates the
    /// shared <paramref name="counts"/> dictionary with per-reason buckets.
    /// </summary>
    private async Task<int> TimeBasedDismissAsync(
        InboxKind kind,
        int? maxUnreadDays,
        int? maxReadDays,
        DateTime now,
        Dictionary<string, int> counts,
        CancellationToken ct)
    {
        var touched = 0;

        if (maxUnreadDays is int maxUnread && maxUnread > 0)
        {
            var cutoff = CutoffFromNowMinusDays(maxUnread);
            var stale = await session.Query<InboxItemView>()
                .Where(i => i.Kind == kind
                         && i.DismissedAt == null
                         && i.ReadAt == null
                         && i.CreatedAt < cutoff)
                .Select(i => i.Id)
                .ToListAsync(ct);

            foreach (var id in stale)
                session.Events.Append(id, new InboxItemDismissedEvent(id, now));

            if (stale.Count > 0)
            {
                counts[$"{kind}.unread-expired"] = stale.Count;
                touched += stale.Count;
            }
        }

        if (maxReadDays is int maxRead && maxRead > 0)
        {
            var cutoff = CutoffFromNowMinusDays(maxRead);
            var stale = await session.Query<InboxItemView>()
                .Where(i => i.Kind == kind
                         && i.DismissedAt == null
                         && i.ReadAt != null
                         && i.ReadAt < cutoff)
                .Select(i => i.Id)
                .ToListAsync(ct);

            foreach (var id in stale)
                session.Events.Append(id, new InboxItemDismissedEvent(id, now));

            if (stale.Count > 0)
            {
                counts[$"{kind}.read-expired"] = stale.Count;
                touched += stale.Count;
            }
        }

        return touched;
    }

    /// <summary>
    /// Marten maps DateTime → `timestamp without time zone`; Npgsql rejects
    /// Kind=UTC in LINQ Where clauses, so cutoff literals must be Unspecified.
    /// </summary>
    private static DateTime CutoffFromNowMinusDays(int days) =>
        DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-days), DateTimeKind.Unspecified);
}
