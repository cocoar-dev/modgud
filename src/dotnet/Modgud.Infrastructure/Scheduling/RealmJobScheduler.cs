using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Quartz;
using Quartz.Impl.Matchers;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Optional realm-lifecycle hook. Scheduling registers an implementation;
/// hosts that use realm provisioning without Quartz simply have no observers.
/// </summary>
public interface IRealmJobScheduleObserver
{
    Task ReconcileAsync(CancellationToken ct = default);
}

/// <summary>
/// Owns the mapping from Modgud's realm/system job model to Quartz identities.
/// Realm jobs use one Quartz group per realm. System jobs share one reserved
/// group and carry the current Control-Plane realm as their tenant context.
/// </summary>
internal sealed class RealmJobScheduler(
    ISchedulerFactory schedulerFactory,
    IJobRegistry registry,
    IGlobalStore globalStore,
    IDocumentStore tenantStore,
    ILogger<RealmJobScheduler> logger) : IRealmJobScheduleObserver
{
    internal const string TenantSlugDataKey = "__modgudTenantSlug";
    internal const string JobScopeDataKey = "__modgudJobScope";
    private const string RealmGroupPrefix = "realm:";
    private const string SystemGroup = "system";

    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct);
        try
        {
            await using var globalSession = globalStore.QuerySession();
            var realms = await globalSession.Query<Realm>()
                .OrderBy(r => r.CreatedAt)
                .ToListAsync(ct);

            var scheduler = await schedulerFactory.GetScheduler(ct);
            await RemoveDeletedRealmGroupsAsync(scheduler, realms, ct);

            foreach (var realm in realms)
            {
                await ReconcileRealmJobsAsync(scheduler, realm, ct);
            }

            await ReconcileSystemJobsAsync(scheduler, realms, ct);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task ApplyAsync(
        JobRegistration registration,
        string realmSlug,
        JobConfig? config,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct);
        try
        {
            var scheduler = await schedulerFactory.GetScheduler(ct);
            await ApplyCoreAsync(scheduler, registration, realmSlug, config, ct);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    internal static JobKey GetJobKey(JobRegistration registration, string realmSlug) =>
        new(registration.Key, GetGroup(registration, realmSlug));

    private static TriggerKey GetTriggerKey(JobRegistration registration, string realmSlug) =>
        new($"{registration.Key}-trigger", GetGroup(registration, realmSlug));

    private static string GetGroup(JobRegistration registration, string realmSlug) =>
        registration.Scope == JobScope.System
            ? SystemGroup
            : $"{RealmGroupPrefix}{realmSlug}";

    private async Task ReconcileRealmJobsAsync(
        IScheduler scheduler,
        Realm realm,
        CancellationToken ct)
    {
        var registrations = registry.All
            .Where(r => r.Scope == JobScope.Realm
                && (realm.IsActive || r.RunWhenRealmInactive))
            .ToList();

        var expectedKeys = registrations
            .Select(r => GetJobKey(r, realm.Slug))
            .ToHashSet();

        var group = $"{RealmGroupPrefix}{realm.Slug}";
        var existingKeys = await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(group), ct);
        var obsoleteKeys = existingKeys.Where(k => !expectedKeys.Contains(k)).ToList();
        if (obsoleteKeys.Count > 0)
            await scheduler.DeleteJobs(obsoleteKeys, ct);

        if (registrations.Count == 0)
            return;

        Dictionary<string, JobConfig> configByKey;
        try
        {
            await using var session = tenantStore.QuerySession(realm.Slug);
            var configs = await session.Query<JobConfig>().ToListAsync(ct);
            configByKey = configs.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "[Jobs] Could not load job configuration for realm {Realm}; its schedules were not reconciled",
                realm.Slug);
            return;
        }

        foreach (var registration in registrations)
        {
            configByKey.TryGetValue(registration.Key, out var config);
            try
            {
                await ApplyCoreAsync(scheduler, registration, realm.Slug, config, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "[Jobs] Failed to schedule {Key} for realm {Realm}",
                    registration.Key, realm.Slug);
            }
        }
    }

    private async Task ReconcileSystemJobsAsync(
        IScheduler scheduler,
        IReadOnlyList<Realm> realms,
        CancellationToken ct)
    {
        var registrations = registry.All.Where(r => r.Scope == JobScope.System).ToList();
        var controlPlanes = realms.Where(r => r.IsControlPlane && r.IsActive).ToList();

        if (controlPlanes.Count != 1)
        {
            var existing = await scheduler.GetJobKeys(
                GroupMatcher<JobKey>.GroupEquals(SystemGroup), ct);
            if (existing.Count > 0)
                await scheduler.DeleteJobs(existing.ToList(), ct);

            logger.LogError(
                "[Jobs] Expected exactly one active Control-Plane realm but found {Count}; system jobs are unscheduled",
                controlPlanes.Count);
            return;
        }

        var controlPlane = controlPlanes[0];
        Dictionary<string, JobConfig> configByKey;
        try
        {
            // System-job configuration belongs to the deployment, not to any
            // tenant database. The current Control Plane controls it, but a
            // transfer must not reset or resurrect another realm's schedule.
            await using var session = globalStore.QuerySession();
            var configs = await session.Query<JobConfig>().ToListAsync(ct);
            configByKey = configs.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "[Jobs] Could not load system-job configuration from Control-Plane realm {Realm}",
                controlPlane.Slug);
            return;
        }

        var expectedKeys = registrations
            .Select(r => GetJobKey(r, controlPlane.Slug))
            .ToHashSet();
        var existingKeys = await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(SystemGroup), ct);
        var obsoleteKeys = existingKeys.Where(k => !expectedKeys.Contains(k)).ToList();
        if (obsoleteKeys.Count > 0)
            await scheduler.DeleteJobs(obsoleteKeys, ct);

        foreach (var registration in registrations)
        {
            configByKey.TryGetValue(registration.Key, out var config);
            try
            {
                await ApplyCoreAsync(scheduler, registration, controlPlane.Slug, config, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "[Jobs] Failed to schedule system job {Key} for Control-Plane realm {Realm}",
                    registration.Key, controlPlane.Slug);
            }
        }
    }

    private async Task ApplyCoreAsync(
        IScheduler scheduler,
        JobRegistration registration,
        string realmSlug,
        JobConfig? config,
        CancellationToken ct)
    {
        var jobKey = GetJobKey(registration, realmSlug);
        var triggerKey = GetTriggerKey(registration, realmSlug);
        var jobDetail = JobBuilder.Create(registration.JobType)
            .WithIdentity(jobKey)
            .WithDescription(registration.Description)
            .UsingJobData(TenantSlugDataKey, realmSlug)
            .UsingJobData(JobScopeDataKey, registration.Scope.ToString())
            .StoreDurably()
            .Build();

        await scheduler.AddJob(jobDetail, replace: true, ct);
        await scheduler.UnscheduleJob(triggerKey, ct);

        if (config is not null && !config.Enabled)
        {
            logger.LogInformation(
                "[Jobs] {Key} is manual-only for realm {Realm} — registered without a trigger",
                registration.Key, realmSlug);
            return;
        }

        var cron = config?.CronOverride ?? registration.DefaultCron;
        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron)
            .Build();
        await scheduler.ScheduleJob(trigger, ct);

        logger.LogInformation(
            "[Jobs] Scheduled {Scope} job {Key} for realm {Realm} with cron '{Cron}'",
            registration.Scope, registration.Key, realmSlug, cron);
    }

    private async Task RemoveDeletedRealmGroupsAsync(
        IScheduler scheduler,
        IReadOnlyCollection<Realm> realms,
        CancellationToken ct)
    {
        var knownGroups = realms
            .Select(r => $"{RealmGroupPrefix}{r.Slug}")
            .ToHashSet(StringComparer.Ordinal);
        var groups = await scheduler.GetJobGroupNames(ct);

        foreach (var group in groups.Where(g =>
                     g.StartsWith(RealmGroupPrefix, StringComparison.Ordinal)
                     && !knownGroups.Contains(g)))
        {
            var keys = await scheduler.GetJobKeys(
                GroupMatcher<JobKey>.GroupEquals(group), ct);
            if (keys.Count > 0)
                await scheduler.DeleteJobs(keys.ToList(), ct);
        }
    }

}
