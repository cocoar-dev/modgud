using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Jobs;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Modgud.Infrastructure.Scheduling;
using Quartz;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Pins the scheduling ownership contract: realm jobs have independent Quartz
/// identities/config/history, while system jobs exist once and are exposed only
/// through the current Control-Plane realm.
/// </summary>
public class ScheduledJobsTenancyTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Realm_schedules_are_independent_and_system_jobs_are_ControlPlane_only()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        const string tenantSlug = "jobs-acme";

        var realms = factory.Services.GetRequiredService<IRealmProvisioningService>();
        var created = await realms.CreateRealmAsync(new CreateRealmDto
        {
            Slug = tenantSlug,
            DisplayName = "Jobs Acme",
            Domains = [$"{tenantSlug}.localhost"],
            InitialAdmin = new InitialAdminDto
            {
                UserName = "admin",
                Email = "admin@jobs-acme.test",
            },
        }, ct);
        Assert.False(created.IsError);

        var scheduler = await factory.Services
            .GetRequiredService<ISchedulerFactory>()
            .GetScheduler(ct);

        var controlPlaneRealmKey = new JobKey(DcrGcJob.Key, "realm:system");
        var tenantRealmKey = new JobKey(DcrGcJob.Key, $"realm:{tenantSlug}");
        var singletonSystemKey = new JobKey(SecurityAuditPruneJob.Key, "system");
        var singletonSystemRetentionKey =
            new JobKey(SystemJobRunHistoryRetentionJob.Key, "system");

        Assert.True(await scheduler.CheckExists(controlPlaneRealmKey, ct));
        Assert.True(await scheduler.CheckExists(tenantRealmKey, ct));
        Assert.True(await scheduler.CheckExists(singletonSystemKey, ct));
        Assert.True(await scheduler.CheckExists(singletonSystemRetentionKey, ct));
        Assert.False(await scheduler.CheckExists(
            new JobKey(SecurityAuditPruneJob.Key, $"realm:{tenantSlug}"), ct));
        Assert.False(await scheduler.CheckExists(
            new JobKey(SystemJobRunHistoryRetentionJob.Key, $"realm:{tenantSlug}"), ct));
        var globalStore = factory.Services.GetRequiredService<IGlobalStore>();

        await InTenantAsync(factory, TenantConstants.SystemTenantId, async jobs =>
        {
            var visible = await jobs.GetAllAsync(ct);
            Assert.Contains(visible,
                j => j.Key == SecurityAuditPruneJob.Key && j.Scope == nameof(JobScope.System));
            Assert.Contains(visible,
                j => j.Key == SystemJobRunHistoryRetentionJob.Key
                    && j.Scope == nameof(JobScope.System));
            Assert.Contains(visible,
                j => j.Key == DcrGcJob.Key && j.Scope == nameof(JobScope.Realm));
            await jobs.UpdateAsync(DcrGcJob.Key, new JobUpdateDto
            {
                CronOverride = "0 0 21 * * ?",
                Enabled = true,
            }, ct);
            await jobs.UpdateAsync(SecurityAuditPruneJob.Key, new JobUpdateDto
            {
                CronOverride = "0 17 1 * * ?",
                Enabled = true,
            }, ct);
            await jobs.TriggerNowAsync(SecurityAuditPruneJob.Key, ct: ct);
        });

        await InTenantAsync(factory, tenantSlug, async jobs =>
        {
            var visible = await jobs.GetAllAsync(ct);
            Assert.DoesNotContain(visible, j => j.Scope == nameof(JobScope.System));
            Assert.Null(await jobs.GetAsync(SecurityAuditPruneJob.Key, ct));
            Assert.Null(await jobs.GetAsync(SystemJobRunHistoryRetentionJob.Key, ct));

            await jobs.UpdateAsync(DcrGcJob.Key, new JobUpdateDto
            {
                CronOverride = "0 0 18 * * ?",
                Enabled = true,
            }, ct);
        });

        Assert.Equal(
            "0 0 21 * * ?",
            await GetCronAsync(scheduler, controlPlaneRealmKey, ct));
        Assert.Equal(
            "0 0 18 * * ?",
            await GetCronAsync(scheduler, tenantRealmKey, ct));

        var systemRun = await WaitForGlobalManualRunAsync(
            factory, SecurityAuditPruneJob.Key, ct);
        Assert.NotNull(systemRun);
        await using (var globalSession = globalStore.QuerySession())
        {
            var systemConfig = await globalSession.LoadAsync<JobConfig>(
                SecurityAuditPruneJob.Key, ct);
            Assert.Equal("0 17 1 * * ?", systemConfig?.CronOverride);
        }

        await using (var tenantMetadataSession = factory.Services
                         .GetRequiredService<IDocumentStore>()
                         .QuerySession(TenantConstants.SystemTenantId))
        {
            Assert.Null(await tenantMetadataSession.LoadAsync<JobConfig>(
                SecurityAuditPruneJob.Key, ct));
            Assert.False(await tenantMetadataSession.Query<JobRunHistoryEntry>()
                .AnyAsync(h => h.JobKey == SecurityAuditPruneJob.Key, ct));
        }

        // Disabled means manual-only: the durable realm job remains, but its
        // own trigger disappears and a manual run still writes tenant history.
        await InTenantAsync(factory, tenantSlug, async jobs =>
        {
            await jobs.UpdateAsync(DcrGcJob.Key, new JobUpdateDto
            {
                CronOverride = "0 0 18 * * ?",
                Enabled = false,
            }, ct);
            await jobs.TriggerNowAsync(DcrGcJob.Key, Guid.NewGuid(), ct);
        });

        Assert.True(await scheduler.CheckExists(tenantRealmKey, ct));
        Assert.DoesNotContain(
            await scheduler.GetTriggersOfJob(tenantRealmKey, ct),
            trigger => trigger is ICronTrigger);

        var tenantRun = await WaitForManualRunAsync(factory, tenantSlug, DcrGcJob.Key, ct);
        Assert.NotNull(tenantRun);

        await using var systemSession = factory.Services
            .GetRequiredService<IDocumentStore>()
            .QuerySession(TenantConstants.SystemTenantId);
        Assert.False(await systemSession.Query<JobRunHistoryEntry>()
            .AnyAsync(h => h.JobKey == DcrGcJob.Key && h.ManualTrigger, ct));

        // Realm lifecycle reconciles the group immediately. Normal realm jobs
        // stop while inactive; private-key hygiene deliberately remains.
        var deactivated = await realms.UpdateRealmAsync(
            tenantSlug, new UpdateRealmDto { IsActive = false }, ct);
        Assert.False(deactivated.IsError);
        Assert.False(await scheduler.CheckExists(tenantRealmKey, ct));
        Assert.True(await scheduler.CheckExists(
            new JobKey(SigningKeyJanitorJob.Key, $"realm:{tenantSlug}"), ct));

        var reactivated = await realms.UpdateRealmAsync(
            tenantSlug, new UpdateRealmDto { IsActive = true }, ct);
        Assert.False(reactivated.IsError);
        Assert.True(await scheduler.CheckExists(tenantRealmKey, ct));
        Assert.DoesNotContain(
            await scheduler.GetTriggersOfJob(tenantRealmKey, ct),
            trigger => trigger is ICronTrigger);

        // Moving the Control-Plane role moves system-job visibility/context,
        // while the reserved Quartz identity remains a single instance.
        var transferred = await realms.TransferControlPlaneAsync(tenantSlug, ct);
        Assert.False(transferred.IsError);
        var systemDetail = await scheduler.GetJobDetail(singletonSystemKey, ct);
        Assert.NotNull(systemDetail);
        Assert.Equal(
            tenantSlug,
            systemDetail!.JobDataMap.GetString("__modgudTenantSlug"));

        await InTenantAsync(factory, TenantConstants.SystemTenantId, async jobs =>
        {
            Assert.DoesNotContain(
                await jobs.GetAllAsync(ct),
                j => j.Scope == nameof(JobScope.System));
        });
        await InTenantAsync(factory, tenantSlug, async jobs =>
        {
            var visible = await jobs.GetAllAsync(ct);
            Assert.Contains(visible,
                j => j.Key == SecurityAuditPruneJob.Key
                    && j.Scope == nameof(JobScope.System)
                    && j.EffectiveCron == "0 17 1 * * ?");
        });
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory,
        string slug,
        Func<IJobsService, Task> action)
    {
        using var tenant = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IJobsService>());
    }

    private static async Task<string?> GetCronAsync(
        IScheduler scheduler,
        JobKey jobKey,
        CancellationToken ct)
    {
        var trigger = Assert.Single(await scheduler.GetTriggersOfJob(jobKey, ct));
        return Assert.IsAssignableFrom<ICronTrigger>(trigger).CronExpressionString;
    }

    private static async Task<JobRunHistoryEntry?> WaitForManualRunAsync(
        ColdStartWebApplicationFactory factory,
        string realmSlug,
        string jobKey,
        CancellationToken ct)
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var session = store.QuerySession(realmSlug);
            var entry = await session.Query<JobRunHistoryEntry>()
                .Where(h => h.JobKey == jobKey && h.ManualTrigger)
                .OrderByDescending(h => h.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (entry is not null)
                return entry;

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        return null;
    }

    private static async Task<JobRunHistoryEntry?> WaitForGlobalManualRunAsync(
        ColdStartWebApplicationFactory factory,
        string jobKey,
        CancellationToken ct)
    {
        var store = factory.Services.GetRequiredService<IGlobalStore>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var session = store.QuerySession();
            var entry = await session.Query<JobRunHistoryEntry>()
                .Where(h => h.JobKey == jobKey && h.ManualTrigger)
                .OrderByDescending(h => h.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (entry is not null)
                return entry;

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        return null;
    }
}
