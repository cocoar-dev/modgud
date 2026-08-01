using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;
using Quartz;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Wraps every Quartz job execution with a <see cref="JobRunHistoryEntry"/>
/// write. Captures start/end timing, success/failure, exception detail and
/// an optional per-run <c>ResultSummary</c> the job can publish via
/// <c>context.Result = "...";</c>.
///
/// Resolves a fresh DI scope inside the owning realm carried by the Quartz
/// job detail. Realm history stays in that tenant DB; system history stays
/// in the non-tenanted global store while notifications resolve in the current
/// Control-Plane realm.
/// </summary>
public class JobRunListener(
    IServiceScopeFactory scopeFactory,
    IGlobalStore globalStore,
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
        if (!context.JobDetail.JobDataMap.TryGetValue(
                RealmJobScheduler.TenantSlugDataKey, out var rawTenant)
            || rawTenant is not string tenantSlug
            || string.IsNullOrWhiteSpace(tenantSlug))
        {
            logger.LogError(
                "[Jobs] Cannot persist run history for {Key}: Quartz job has no owning realm",
                context.JobDetail.Key);
            return;
        }

        if (!context.JobDetail.JobDataMap.TryGetValue(
                RealmJobScheduler.JobScopeDataKey, out var rawScope)
            || rawScope is not string scopeName
            || !Enum.TryParse<JobScope>(scopeName, out var jobScope))
        {
            logger.LogError(
                "[Jobs] Cannot persist run history for {Key}: Quartz job has no valid ownership scope",
                context.JobDetail.Key);
            return;
        }

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

        using var tenant = TenantContext.Enter(tenantSlug);
        using var scope = scopeFactory.CreateScope();
        try
        {
            if (jobScope == JobScope.System)
            {
                await using var systemSession = globalStore.LightweightSession();
                systemSession.Store(entry);
                await systemSession.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var realmSession = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                realmSession.Store(entry);
                await realmSession.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Persisting history must never crash the listener — log and move on.
            logger.LogWarning(ex,
                "[Jobs] Failed to persist run history for {Key} in realm {Realm}",
                key, tenantSlug);
        }

        // Inbox-side notify: failures → admins, manual completions → trigger user.
        // Same defensive shape as the history write — a notify failure must
        // not crash the scheduler.
        try
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IJobRunNotifier>();
            await notifier.NotifyAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Jobs] Job-run notify failed for {Key} in realm {Realm}",
                key, tenantSlug);
        }
    }

    private static string Firstline(string s)
    {
        var idx = s.IndexOf('\n');
        return idx >= 0 ? s[..idx].TrimEnd('\r') : s;
    }
}
