using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cocoar.Auth.Infrastructure.Scheduling;

/// <summary>
/// Wraps every Quartz job execution with a <see cref="JobRunHistoryEntry"/>
/// write. Captures start/end timing, success/failure, exception detail and
/// an optional per-run <c>ResultSummary</c> the job can publish via
/// <c>context.Result = "...";</c>.
///
/// Resolves a fresh DI scope each run so the IDocumentSession is short-lived
/// and not entangled with whatever the job itself uses.
/// </summary>
public class JobRunListener(
    IServiceScopeFactory scopeFactory,
    ILogger<JobRunListener> logger) : IJobListener
{
    public string Name => nameof(JobRunListener);

    private static readonly string StartTimeKey = "__startedAtUtc";
    internal const string ManualTriggerKey = "manual";
    internal const string TriggeredByUserIdKey = "triggeredBy";

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        context.Put(StartTimeKey, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task JobWasExecuted(
        IJobExecutionContext context,
        JobExecutionException? jobException,
        CancellationToken cancellationToken = default)
    {
        var key = context.JobDetail.Key.Name;
        var startedAt = context.Get(StartTimeKey) as DateTime? ?? context.FireTimeUtc.UtcDateTime;
        var finishedAt = DateTime.UtcNow;
        var manual = context.MergedJobDataMap.TryGetValue(ManualTriggerKey, out var m) && m is true;
        var triggeredBy = context.MergedJobDataMap.TryGetValue(TriggeredByUserIdKey, out var t) && t is Guid g
            ? g
            : (Guid?)null;
        var resultSummary = context.Result as string;

        var entry = new JobRunHistoryEntry
        {
            Id = Guid.NewGuid(),
            JobKey = key,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationMs = (long)(finishedAt - startedAt).TotalMilliseconds,
            Success = jobException is null,
            ErrorMessage = jobException is null ? null : Firstline(jobException.GetBaseException().Message),
            ExceptionDetail = jobException?.ToString(),
            ResultSummary = resultSummary,
            ManualTrigger = manual,
            TriggeredByUserId = triggeredBy,
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(entry);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Persisting history must never crash the listener — log and move on.
            logger.LogWarning(ex, "[Jobs] Failed to persist run history for {Key}", key);
        }
    }

    private static string Firstline(string s)
    {
        var idx = s.IndexOf('\n');
        return idx >= 0 ? s[..idx].TrimEnd('\r') : s;
    }
}
