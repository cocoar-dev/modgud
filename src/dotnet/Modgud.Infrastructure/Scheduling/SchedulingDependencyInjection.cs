using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Spi;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.Scheduling;

public static class SchedulingDependencyInjection
{
    /// <summary>
    /// Wire Quartz.NET with an in-memory job store, register the
    /// <see cref="IJobsService"/> facade and the run-history listener. Hosts
    /// register every compiled job explicitly as realm-owned or system-owned;
    /// a hosted bootstrap materialises the corresponding Quartz instances.
    /// </summary>
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
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
            // In-memory store — schedules don't survive restart. The
            // startup-bootstrap re-applies overrides from Marten on every
            // boot so the effective state is identical.
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
