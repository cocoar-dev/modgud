namespace Modgud.Application.Scheduling;

/// <summary>
/// Periodic sweep that trims the <c>JobRunHistoryEntry</c> document table so
/// it doesn't grow unbounded. Two independent caps — admins can use either
/// or both. Driven by <c>JobRunHistoryRetentionJob</c> on the Quartz schedule.
/// </summary>
public interface IJobRunHistoryRetentionService
{
    Task<JobRunHistoryRetentionResult> ExecuteAsync(JobRunHistoryRetentionConfig config, CancellationToken ct = default);
}

/// <summary>
/// Effective retention policy for a single run.
/// </summary>
/// <param name="MaxAgeDays">
/// Delete entries whose <c>StartedAt</c> is older than this many days.
/// <c>null</c> = skip the age sweep.
/// </param>
/// <param name="MaxEntriesPerJob">
/// Keep at most this many newest entries per job-key. <c>null</c> = unlimited.
/// </param>
public sealed record JobRunHistoryRetentionConfig(
    int? MaxAgeDays,
    int? MaxEntriesPerJob);

public sealed record JobRunHistoryRetentionResult(
    int DeletedByAge,
    int DeletedByCount);
