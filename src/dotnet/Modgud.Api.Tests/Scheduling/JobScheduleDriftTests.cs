using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Scheduling;
using Quartz;

namespace Modgud.Api.Tests.Scheduling;

/// <summary>
/// B9-JS-01 — a job's saved configuration and its Quartz trigger were reconciled only at
/// startup. An apply that failed after <c>UnscheduleJob</c> (or two updates applied in the
/// other order) left an enabled job without its trigger, and the admin surface still showed it
/// enabled. The overview now reports the deviation, and a periodic reconcile repairs it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class JobScheduleDriftTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string JobKey = "session-prune";

    [Fact]
    public async Task An_enabled_job_without_its_trigger_is_reported_and_repaired()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await DriftAsync(ct));

        // The state an apply leaves when it fails between UnscheduleJob and ScheduleJob.
        var scheduler = await Factory.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(ct);
        Assert.True(await scheduler.UnscheduleJob(new TriggerKey($"{JobKey}-trigger", "realm:system"), ct));

        Assert.Equal("TriggerMissing", await DriftAsync(ct));

        // The periodic reconcile repairs exactly that, and only that.
        Assert.Contains(Factory.Services.GetServices<IHostedService>(), s => s.GetType().Name == "JobScheduleDriftMonitor");
        Assert.Equal(1, await Factory.Services.GetRequiredService<IJobScheduleReconciler>().RepairDriftAsync(ct));
        Assert.Null(await DriftAsync(ct));
        Assert.NotNull(await scheduler.GetTrigger(new TriggerKey($"{JobKey}-trigger", "realm:system"), ct));
        Assert.Equal(0, await Factory.Services.GetRequiredService<IJobScheduleReconciler>().RepairDriftAsync(ct));
    }

    /// <summary>
    /// R4 (post-merge review) — Quartz keeps a cron expression upper-cased. A lower-case override
    /// (<c>mon-fri</c>) is the same schedule and must not count as drift, otherwise the periodic
    /// reconcile re-applies it every five minutes.
    /// </summary>
    [Fact]
    public async Task A_lower_case_cron_override_is_not_drift()
    {
        var ct = TestContext.Current.CancellationToken;
        var update = await Client.PutAsJsonAsync($"/api/admin/jobs/{JobKey}",
            new { CronOverride = "0 15 3 ? * mon-fri" }, ct);
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync(ct));
        try
        {
            Assert.Null(await DriftAsync(ct));
            Assert.Equal(0, await Factory.Services.GetRequiredService<IJobScheduleReconciler>().RepairDriftAsync(ct));
        }
        finally
        {
            await Client.PutAsJsonAsync($"/api/admin/jobs/{JobKey}", new { CronOverride = (string?)null }, ct);
        }
    }

    private async Task<string?> DriftAsync(CancellationToken ct)
    {
        using var response = await Client.GetAsync($"/api/admin/jobs/{JobKey}", ct);
        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(job.GetProperty("Enabled").GetBoolean());
        return job.TryGetProperty("ScheduleDrift", out var drift) && drift.ValueKind == JsonValueKind.String
            ? drift.GetString()
            : null;
    }
}
