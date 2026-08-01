using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Application.Scheduling;

namespace Modgud.Infrastructure.Scheduling;

/// <inheritdoc />
public class JobRunHistoryRetentionService(
    IDocumentSession session,
    ILogger<JobRunHistoryRetentionService> logger) : IJobRunHistoryRetentionService
{
    public Task<JobRunHistoryRetentionResult> ExecuteAsync(
        JobRunHistoryRetentionConfig config,
        CancellationToken ct = default) =>
        ExecuteAsync(session, config, logger, ct);

    /// <summary>
    /// Store-agnostic retention core. Realm jobs pass their tenant session;
    /// the system retention job passes a global-store session.
    /// </summary>
    public static async Task<JobRunHistoryRetentionResult> ExecuteAsync(
        IDocumentSession target,
        JobRunHistoryRetentionConfig config,
        ILogger logger,
        CancellationToken ct = default)
    {
        var deletedByAge = 0;
        var deletedByCount = 0;

        // 1. Age sweep (single bulk delete).
        if (config.MaxAgeDays is int maxAge && maxAge > 0)
        {
            // Marten maps DateTime → timestamp without time zone, so the
            // literal must be Kind=Unspecified to avoid Npgsql's mixed-kind error.
            var cutoff = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-maxAge), DateTimeKind.Unspecified);
            var ids = await target.Query<JobRunHistoryEntry>()
                .Where(e => e.StartedAt < cutoff)
                .Select(e => e.Id)
                .ToListAsync(ct);
            foreach (var id in ids) target.Delete<JobRunHistoryEntry>(id);
            deletedByAge = ids.Count;
        }

        // 2. Per-job count cap. Pull just (Id, JobKey, StartedAt) and
        // partition in-memory — the history table is small enough that one
        // round-trip per pass is cheaper than N grouped subqueries.
        if (config.MaxEntriesPerJob is int maxPerJob && maxPerJob > 0)
        {
            var all = await target.Query<JobRunHistoryEntry>()
                .Select(e => new { e.Id, e.JobKey, e.StartedAt })
                .ToListAsync(ct);
            var stale = all
                .GroupBy(x => x.JobKey)
                .SelectMany(g => g.OrderByDescending(x => x.StartedAt).Skip(maxPerJob))
                .Select(x => x.Id)
                .ToList();
            foreach (var id in stale) target.Delete<JobRunHistoryEntry>(id);
            deletedByCount = stale.Count;
        }

        if (deletedByAge + deletedByCount > 0)
        {
            await target.SaveChangesAsync(ct);
            logger.LogInformation(
                "[Jobs:HistoryRetention] Deleted {ByAge} by age, {ByCount} by count",
                deletedByAge, deletedByCount);
        }

        return new JobRunHistoryRetentionResult(deletedByAge, deletedByCount);
    }
}
