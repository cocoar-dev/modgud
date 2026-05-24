using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Spi;
using Cocoar.Auth.Application.Scheduling;

namespace Cocoar.Auth.Infrastructure.Scheduling;

public static class SchedulingDependencyInjection
{
    /// <summary>
    /// Wire Quartz.NET with an in-memory job store, register the
    /// <see cref="IJobsService"/> facade and the run-history listener. The
    /// host that calls this is responsible for calling
    /// <c>AddSystemJob&lt;TJob&gt;(...)</c> for each compiled job to register
    /// it with <see cref="IJobRegistry"/>; everything is scheduled inside
    /// a hosted bootstrap step.
    /// </summary>
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        services.AddSingleton<IJobRegistry, JobRegistry>();
        services.AddScoped<IJobsService, JobsService>();
        services.AddScoped<IJobRunHistoryRetentionService, JobRunHistoryRetentionService>();
        // No-op default; hosts that want job→inbox notifications override
        // this binding (Cocoar.Auth.Api does so in Program.cs).
        services.AddScoped<IJobRunNotifier, NoopJobRunNotifier>();
        services.AddSingleton<JobRunListener>();

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

        // Boot step: after the host has started but before HTTP requests arrive,
        // walk the JobRegistry, apply Marten JobConfig overrides, and schedule
        // each job in Quartz. Also attach the JobRunListener at this point so
        // it sees every subsequent execution.
        services.AddHostedService<SchedulingBootstrap>();

        return services;
    }

    /// <summary>
    /// Register a compiled job type. Call once per job at startup.
    /// </summary>
    public static IServiceCollection AddSystemJob<TJob>(
        this IServiceCollection services,
        string key,
        string name,
        string defaultCron,
        string? description = null,
        Func<IReadOnlyList<JobParameterField>>? getParameterSchema = null)
        where TJob : class, IJob
    {
        services.AddTransient<TJob>();   // resolved by MicrosoftDependencyInjectionJobFactory
        services.AddSingleton(new JobRegistration
        {
            Key = key,
            Name = name,
            Description = description,
            DefaultCron = defaultCron,
            JobType = typeof(TJob),
            Kind = JobKind.System,
            GetParameterSchema = getParameterSchema,
        });
        return services;
    }
}

/// <summary>
/// Quartz job factory backed by Microsoft.Extensions.DependencyInjection.
/// Creates a scope per job execution so scoped services (IDocumentSession,
/// IJobRunHistoryRetentionService) work correctly.
/// </summary>
internal sealed class MicrosoftDependencyInjectionJobFactory(IServiceProvider rootProvider) : IJobFactory
{
    public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
    {
        var scope = rootProvider.CreateScope();
        var job = (IJob)scope.ServiceProvider.GetRequiredService(bundle.JobDetail.JobType);
        // Attach the scope so we can dispose it when the job returns.
        return new ScopedJobWrapper(job, scope);
    }

    public void ReturnJob(IJob job)
    {
        if (job is ScopedJobWrapper wrapper) wrapper.Dispose();
    }

    private sealed class ScopedJobWrapper(IJob inner, IServiceScope scope) : IJob, IDisposable
    {
        public Task Execute(IJobExecutionContext context) => inner.Execute(context);
        public void Dispose() => scope.Dispose();
    }
}

/// <summary>
/// Reads <see cref="JobRegistration"/> + <see cref="JobConfig"/> at startup,
/// schedules each enabled job, and attaches the run-history listener to the
/// scheduler. Idempotent — also handles re-registration on hot-reload of the
/// host.
/// </summary>
internal sealed class SchedulingBootstrap(
    ISchedulerFactory schedulerFactory,
    IJobRegistry registry,
    IServiceScopeFactory scopeFactory,
    JobRunListener listener,
    ILogger<SchedulingBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Pull config overrides up-front so we only open one session.
        Dictionary<string, JobConfig> configByKey;
        using (var scope = scopeFactory.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var configs = await session.Query<JobConfig>().ToListAsync(cancellationToken);
            configByKey = configs.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        }

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        scheduler.ListenerManager.AddJobListener(listener);

        foreach (var reg in registry.All)
        {
            configByKey.TryGetValue(reg.Key, out var cfg);
            try
            {
                await ApplyAsync(scheduler, reg, cfg, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Jobs] Failed to schedule {Key}", reg.Key);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ApplyAsync(IScheduler scheduler, JobRegistration reg, JobConfig? cfg, CancellationToken ct)
    {
        var jobKey = new JobKey(reg.Key);
        var jobDetail = JobBuilder.Create(reg.JobType)
            .WithIdentity(jobKey)
            .WithDescription(reg.Description)
            .StoreDurably()
            .Build();
        await scheduler.AddJob(jobDetail, replace: true, ct);

        if (cfg is not null && !cfg.Enabled)
        {
            logger.LogInformation("[Jobs] {Key} is disabled — registered but unscheduled", reg.Key);
            return;
        }

        var cron = cfg?.CronOverride ?? reg.DefaultCron;
        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{reg.Key}-trigger")
            .ForJob(jobKey)
            .WithCronSchedule(cron)
            .Build();
        await scheduler.ScheduleJob(trigger, ct);
        logger.LogInformation("[Jobs] Scheduled {Key} with cron '{Cron}'", reg.Key, cron);
    }
}
