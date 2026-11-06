using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>How often <see cref="JobScheduleDriftMonitor"/> compares schedules.</summary>
public sealed class JobScheduleReconcileOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// B9-JS-01 — the schedules were reconciled only at startup, so a failed or out-of-order apply
/// lasted until the next restart. This compares them periodically and repairs what deviates.
/// Every node runs it; the scheduler's cluster lock serialises them, and a schedule that matches
/// is only read, never rewritten.
/// </summary>
internal sealed class JobScheduleDriftMonitor(
    IJobScheduleReconciler reconciler,
    JobScheduleReconcileOptions options,
    ILogger<JobScheduleDriftMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await reconciler.RepairDriftAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[Jobs] Comparing the job schedules with their configuration failed");
            }
        }
    }
}
