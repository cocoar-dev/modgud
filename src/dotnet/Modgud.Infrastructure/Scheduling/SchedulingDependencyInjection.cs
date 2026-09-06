using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Spi;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// How the Quartz scheduler stores its jobs and triggers (ADR 0022, D4).
/// </summary>
public sealed class SchedulingStoreOptions
{
    /// <summary>
    /// Master-database connection string for the clustered Postgres job store.
    /// <c>null</c> keeps the in-memory store (Development, Testing).
    /// </summary>
    public string? PersistentStoreConnectionString { get; init; }

    /// <summary>
    /// Shared scheduler name. Every node of one deployment must use the same
    /// name — Quartz clusters instances by it.
    /// </summary>
    public string SchedulerName { get; init; } = "modgud";

    /// <summary>
    /// How often a clustered node stamps its check-in row. A node whose
    /// check-in is older than this plus the misfire threshold is treated as
    /// dead and its in-flight, recoverable jobs are re-run by a survivor.
    /// </summary>
    public TimeSpan ClusterCheckinInterval { get; init; } = TimeSpan.FromSeconds(7.5);

    public bool IsPersistent => !string.IsNullOrWhiteSpace(PersistentStoreConnectionString);
}

public static class SchedulingDependencyInjection
{
    /// <summary>
    /// Wire Quartz.NET, register the <see cref="IJobsService"/> facade and the
    /// run-history listener. Hosts register every compiled job explicitly as
    /// realm-owned or system-owned; a hosted bootstrap materialises the
    /// corresponding Quartz instances.
    /// <para>
    /// With a persistent store the scheduler is clustered: every node runs a
    /// scheduler instance against the shared Postgres tables, Quartz elects
    /// which instance fires a trigger, honours <c>[DisallowConcurrentExecution]</c>
    /// cluster-wide, and recovers <c>RequestRecovery</c> jobs from a dead node.
    /// Without one (Development, Testing) the in-memory store is used and the
    /// schedules are rebuilt from <see cref="JobConfig"/> on every boot.
    /// </para>
    /// </summary>
    public static IServiceCollection AddScheduling(
        this IServiceCollection services,
        SchedulingStoreOptions? storeOptions = null)
    {
        storeOptions ??= new SchedulingStoreOptions();
        services.AddSingleton(storeOptions);

        services.AddSingleton<IJobRegistry, JobRegistry>();
        services.AddScoped<IJobsService, JobsService>();
        services.AddScoped<IJobRunHistoryRetentionService, JobRunHistoryRetentionService>();
        // No-op default; hosts that want job→inbox notifications override
        // this binding (Modgud.Api does so in Program.cs).
        services.AddScoped<IJobRunNotifier, NoopJobRunNotifier>();
        services.AddSingleton<JobRunListener>();
        services.AddSingleton<RealmJobScheduler>();
        services.AddSingleton<IRealmJobScheduleObserver>(
            sp => sp.GetRequiredService<RealmJobScheduler>());

        services.AddQuartz(q =>
        {
            q.SchedulerName = storeOptions.SchedulerName;

            if (!storeOptions.IsPersistent)
            {
                // In-memory store — schedules don't survive restart. The
                // startup-bootstrap re-applies overrides from Marten on every
                // boot so the effective state is identical.
                return;
            }

            // AUTO gives every node a unique instance id; the shared name is
            // what makes them one cluster.
            q.SchedulerId = "AUTO";
            q.UsePersistentStore(store =>
            {
                // String-only JobDataMap: every value we put in (tenant slug,
                // scope, manual-trigger flag, triggering user) is a string, so
                // the map round-trips without a binary serializer.
                store.UseProperties = true;
                store.RetryInterval = TimeSpan.FromSeconds(15);
                store.UsePostgres(pg =>
                {
                    pg.ConnectionString = storeOptions.PersistentStoreConnectionString!;
                    // Tables live in their own schema of the master database,
                    // created by QuartzSchemaBootstrap before the scheduler starts.
                    pg.TablePrefix = $"{QuartzSchemaBootstrap.Schema}.qrtz_";
                });
                store.UseClustering(cluster =>
                {
                    cluster.CheckinInterval = storeOptions.ClusterCheckinInterval;
                    cluster.CheckinMisfireThreshold = TimeSpan.FromSeconds(60);
                });
                store.UseSystemTextJsonSerializer();
            });
        });

        services.AddQuartzHostedService(o =>
        {
            // Wait for jobs to finish on shutdown so we don't leave half-written
            // history entries.
            o.WaitForJobsToComplete = true;
        });

        // Quartz needs a DI-aware job factory so [DisallowConcurrentExecution]
        // jobs can pull dependencies (e.g. IDocumentStore) from scope.
        services.AddSingleton<IJobFactory, MicrosoftDependencyInjectionJobFactory>();

        // Boot step: after the host has started, reconcile every realm's
        // independent schedule and the single Control-Plane system schedule.
        services.AddHostedService<SchedulingBootstrap>();

        return services;
    }

    /// <summary>
    /// Register a compiled job that gets one independent Quartz job and trigger
    /// per realm.
    /// </summary>
    public static IServiceCollection AddRealmJob<TJob>(
        this IServiceCollection services,
        string key,
        string name,
        string defaultCron,
        string? description = null,
        Func<IReadOnlyList<JobParameterField>>? getParameterSchema = null,
        bool runWhenRealmInactive = false)
        where TJob : class, IJob
        => AddJob<TJob>(
            services,
            key,
            name,
            defaultCron,
            JobScope.Realm,
            description,
            getParameterSchema,
            runWhenRealmInactive);

    /// <summary>
    /// Register one deployment-wide compiled job. It is scheduled once and is
    /// visible/configurable only in the current Control-Plane realm.
    /// </summary>
    public static IServiceCollection AddSystemJob<TJob>(
        this IServiceCollection services,
        string key,
        string name,
        string defaultCron,
        string? description = null,
        Func<IReadOnlyList<JobParameterField>>? getParameterSchema = null)
        where TJob : class, IJob
        => AddJob<TJob>(
            services,
            key,
            name,
            defaultCron,
            JobScope.System,
            description,
            getParameterSchema,
            runWhenRealmInactive: false);

    private static IServiceCollection AddJob<TJob>(
        IServiceCollection services,
        string key,
        string name,
        string defaultCron,
        JobScope scope,
        string? description,
        Func<IReadOnlyList<JobParameterField>>? getParameterSchema,
        bool runWhenRealmInactive)
        where TJob : class, IJob
    {
        services.AddTransient<TJob>();
        services.AddSingleton(new JobRegistration
        {
            Key = key,
            Name = name,
            Description = description,
            DefaultCron = defaultCron,
            JobType = typeof(TJob),
            Kind = JobKind.System,
            Scope = scope,
            RunWhenRealmInactive = runWhenRealmInactive,
            GetParameterSchema = getParameterSchema,
        });
        return services;
    }
}

/// <summary>
/// Quartz job factory backed by Microsoft.Extensions.DependencyInjection.
/// Resolves the actual job only after entering the tenant carried by the
/// Quartz job detail. This guarantees constructor-injected scoped services
/// bind to the owning realm, even though there is no HTTP request.
/// </summary>
internal sealed class MicrosoftDependencyInjectionJobFactory(IServiceProvider rootProvider) : IJobFactory
{
    public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
    {
        if (!bundle.JobDetail.JobDataMap.TryGetValue(
                RealmJobScheduler.TenantSlugDataKey, out var rawTenant)
            || rawTenant is not string tenantSlug
            || string.IsNullOrWhiteSpace(tenantSlug))
        {
            throw new InvalidOperationException(
                $"Scheduled job '{bundle.JobDetail.Key}' has no owning realm.");
        }

        return new TenantScopedJob(rootProvider, bundle.JobDetail.JobType, tenantSlug);
    }

    public void ReturnJob(IJob job) { }

    private sealed class TenantScopedJob(
        IServiceProvider provider,
        Type jobType,
        string tenantSlug) : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            using var tenant = TenantContext.Enter(tenantSlug);
            using var scope = provider.CreateScope();
            var inner = (IJob)scope.ServiceProvider.GetRequiredService(jobType);
            await inner.Execute(context);
        }
    }
}

/// <summary>
/// Reconciles all realm/system job instances at startup and attaches the
/// run-history listener.
/// </summary>
internal sealed class SchedulingBootstrap(
    ISchedulerFactory schedulerFactory,
    RealmJobScheduler jobScheduler,
    JobRunListener listener) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        scheduler.ListenerManager.AddJobListener(listener);
        await jobScheduler.ReconcileAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
