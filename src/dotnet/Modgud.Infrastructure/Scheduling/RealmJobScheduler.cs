using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Cluster;
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
/// B9-JS-01 — compares every job's saved configuration with its Quartz job and trigger and
/// repairs only what deviates (an apply that failed half-way, a schedule change rolled back after
/// Quartz already took it). Run periodically by <see cref="JobScheduleDriftMonitor"/>.
/// </summary>
public interface IJobScheduleReconciler
{
    /// <summary>Returns the number of jobs whose schedule was repaired.</summary>
    Task<int> RepairDriftAsync(CancellationToken ct = default);
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
    IClusterLock clusterLock,
    ILogger<RealmJobScheduler> logger) : IRealmJobScheduleObserver, IJobScheduleReconciler
{
    internal const string TenantSlugDataKey = "__modgudTenantSlug";
    internal const string JobScopeDataKey = "__modgudJobScope";
    private const string RealmGroupPrefix = "realm:";
    private const string SystemGroup = "system";

    // Serialises mutations within this process; the cluster lock below does the
    // same across nodes that share the persistent job store (ADR 0022, D4) —
    // two booting nodes reconciling the same realm would otherwise race on
    // AddJob/ScheduleJob and one of them would fail with "already exists".
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    /// <summary>Writes every job and trigger anew (startup, realm lifecycle): also picks up a
    /// changed job type or description of a new build.</summary>
    public Task ReconcileAsync(CancellationToken ct = default) => ReconcileAllAsync(onlyDrifted: false, ct);

    public Task<int> RepairDriftAsync(CancellationToken ct = default) => ReconcileAllAsync(onlyDrifted: true, ct);

    private async Task<int> ReconcileAllAsync(bool onlyDrifted, CancellationToken ct)
    {
        await _mutationLock.WaitAsync(ct);
        try
        {
            await using var _ = await clusterLock.AcquireAsync(ClusterLockKeys.QuartzSchedules, ct);
            await using var globalSession = globalStore.QuerySession();
            var realms = await globalSession.Query<Realm>()
                .OrderBy(r => r.CreatedAt)
                .ToListAsync(ct);

            var scheduler = await schedulerFactory.GetScheduler(ct);
            await RemoveDeletedRealmGroupsAsync(scheduler, realms, ct);

            var repaired = 0;
            foreach (var realm in realms)
            {
                repaired += await ReconcileRealmJobsAsync(scheduler, realm, onlyDrifted, ct);
            }

            repaired += await ReconcileSystemJobsAsync(scheduler, realms, onlyDrifted, ct);
            return repaired;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Applies the job's configuration as it is saved NOW, read inside the lock: of two updates
    /// that race, the one applied last applies the latest saved state, so Quartz cannot keep an
    /// older one (B9-JS-01). Call it after the configuration was committed.
    /// </summary>
    public async Task ApplyAsync(
        JobRegistration registration,
        string realmSlug,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct);
        try
        {
            await using var _ = await clusterLock.AcquireAsync(ClusterLockKeys.QuartzSchedules, ct);
            JobConfig? config;
            if (registration.Scope == JobScope.System)
            {
                await using var session = globalStore.QuerySession();
                config = await session.LoadAsync<JobConfig>(registration.Key, ct);
            }
            else
            {
                await using var session = tenantStore.QuerySession(realmSlug);
                config = await session.LoadAsync<JobConfig>(registration.Key, ct);
            }
            var scheduler = await schedulerFactory.GetScheduler(ct);
            await ApplyCoreAsync(scheduler, registration, realmSlug, config, ct);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// How the Quartz state of a job deviates from its saved configuration, or null when it
    /// matches: <c>JobMissing</c>, <c>TriggerMissing</c> (enabled, no schedule),
    /// <c>UnexpectedTrigger</c> (manual-only, still scheduled) or <c>CronMismatch</c>.
    /// A one-off trigger of a manual run is not part of the comparison.
    /// </summary>
    internal static async Task<string?> DescribeDriftAsync(
        IScheduler scheduler,
        JobRegistration registration,
        string realmSlug,
        JobConfig? config,
        CancellationToken ct)
    {
        var jobKey = GetJobKey(registration, realmSlug);
        if (!await scheduler.CheckExists(jobKey, ct)) return "JobMissing";

        var triggerKey = GetTriggerKey(registration, realmSlug);
        var trigger = await scheduler.GetTrigger(triggerKey, ct);
        if (config is not null && !config.Enabled)
            return trigger is null ? null : "UnexpectedTrigger";
        if (trigger is null) return "TriggerMissing";

        var cron = config?.CronOverride ?? registration.DefaultCron;
        return trigger is ICronTrigger cronTrigger
               && string.Equals(cronTrigger.CronExpressionString, cron, StringComparison.Ordinal)
               && cronTrigger.JobKey.Equals(jobKey)
            ? null
            : "CronMismatch";
    }

    /// <summary>Applies one job: always, or only when its Quartz state deviates. Returns whether
    /// it repaired a deviation.</summary>
    private async Task<bool> ReconcileJobAsync(
        IScheduler scheduler,
        JobRegistration registration,
        string realmSlug,
        JobConfig? config,
        bool onlyDrifted,
        CancellationToken ct)
    {
        if (!onlyDrifted)
        {
            await ApplyCoreAsync(scheduler, registration, realmSlug, config, ct);
            return false;
        }

        var drift = await DescribeDriftAsync(scheduler, registration, realmSlug, config, ct);
        if (drift is null) return false;
        await ApplyCoreAsync(scheduler, registration, realmSlug, config, ct);
        logger.LogWarning(
            "[Jobs] Repaired the schedule of {Key} for realm {Realm}: {Drift}",
            registration.Key, realmSlug, drift);
        return true;
    }

    internal static JobKey GetJobKey(JobRegistration registration, string realmSlug) =>
        new(registration.Key, GetGroup(registration, realmSlug));

    private static TriggerKey GetTriggerKey(JobRegistration registration, string realmSlug) =>
        new($"{registration.Key}-trigger", GetGroup(registration, realmSlug));

    private static string GetGroup(JobRegistration registration, string realmSlug) =>
        registration.Scope == JobScope.System
            ? SystemGroup
            : $"{RealmGroupPrefix}{realmSlug}";

    private async Task<int> ReconcileRealmJobsAsync(
        IScheduler scheduler,
        Realm realm,
        bool onlyDrifted,
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
            return 0;

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
            return 0;
        }

        var repaired = 0;
        foreach (var registration in registrations)
        {
            configByKey.TryGetValue(registration.Key, out var config);
            try
            {
                if (await ReconcileJobAsync(scheduler, registration, realm.Slug, config, onlyDrifted, ct))
                    repaired++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "[Jobs] Failed to schedule {Key} for realm {Realm}",
                    registration.Key, realm.Slug);
            }
        }
        return repaired;
    }

    private async Task<int> ReconcileSystemJobsAsync(
        IScheduler scheduler,
        IReadOnlyList<Realm> realms,
        bool onlyDrifted,
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

            if (realms.Count == 0)
            {
                logger.LogInformation(
                    "[Jobs] No realm exists yet; deployment-wide jobs remain unscheduled until first installation");
            }
            else
            {
                logger.LogError(
                    "[Jobs] Expected exactly one active Control-Plane realm but found {Count}; system jobs are unscheduled",
                    controlPlanes.Count);
            }
            return 0;
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
            return 0;
        }

        var expectedKeys = registrations
            .Select(r => GetJobKey(r, controlPlane.Slug))
            .ToHashSet();
        var existingKeys = await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(SystemGroup), ct);
        var obsoleteKeys = existingKeys.Where(k => !expectedKeys.Contains(k)).ToList();
        if (obsoleteKeys.Count > 0)
            await scheduler.DeleteJobs(obsoleteKeys, ct);

        var repaired = 0;
        foreach (var registration in registrations)
        {
            configByKey.TryGetValue(registration.Key, out var config);
            try
            {
                if (await ReconcileJobAsync(scheduler, registration, controlPlane.Slug, config, onlyDrifted, ct))
                    repaired++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "[Jobs] Failed to schedule system job {Key} for Control-Plane realm {Realm}",
                    registration.Key, controlPlane.Slug);
            }
        }
        return repaired;
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
            // Every job is an idempotent sweep: if the node executing it dies
            // mid-run, a surviving node re-runs it (clustered store only).
            .RequestRecovery()
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
