using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Scheduling;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Trims deployment-wide system-job history in the non-tenanted global store.
/// This is intentionally a separate system job: no realm-owned retention job
/// may read or mutate platform job metadata.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SystemJobRunHistoryRetentionJob(
    IGlobalStore globalStore,
    ILogger<SystemJobRunHistoryRetentionJob> logger) : IJob
{
    public const string Key = "system-job-run-history-retention";
    public const string Name = "System Job-Run-History Retention";
    public const string Description =
        "Trims deployment-wide system-job run history in the non-tenanted global store.";

    /// <summary>03:45 UTC daily, after realm history retention.</summary>
    public const string DefaultCron = "0 45 3 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        await using var session = globalStore.LightweightSession();
        var config = await JobRunHistoryRetentionJob.BuildConfigAsync(session, Key, ct);
        var result = await JobRunHistoryRetentionService.ExecuteAsync(
            session, config, logger, ct);

        var total = result.DeletedByAge + result.DeletedByCount;
        context.Result = total == 0
            ? "Nothing to delete"
            : $"Deleted {total} system-job entries (age: {result.DeletedByAge}, count: {result.DeletedByCount})";
    }
}
