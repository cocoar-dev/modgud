using Marten;
using Microsoft.Extensions.Logging;
using Quartz;
using Cocoar.Auth.Application.Scheduling;

namespace Cocoar.Auth.Infrastructure.Scheduling;

/// <inheritdoc />
/// <remarks>
/// Pulls registrations from <see cref="IJobRegistry"/>, overrides from
/// Marten (<see cref="JobConfig"/>), live schedule state from Quartz, and
/// run history from Marten. The single non-trivial bit is the cron-reschedule
/// path — we delete and recreate the trigger to keep semantics simple.
/// </remarks>
public class JobsService(
    IJobRegistry registry,
    ISchedulerFactory schedulerFactory,
    IDocumentSession session,
    ILogger<JobsService> logger) : IJobsService
{
    public async Task<IReadOnlyList<JobOverviewDto>> GetAllAsync(CancellationToken ct = default)
    {
        var configs = await session.Query<JobConfig>().ToListAsync(ct);
        var configByKey = configs.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        // Latest run per key, in a single query.
        var registrationKeys = registry.All.Select(r => r.Key).ToList();
        var allHistory = registrationKeys.Count == 0
            ? new List<JobRunHistoryEntry>()
            : await session.Query<JobRunHistoryEntry>()
                .Where(h => h.JobKey.IsOneOf(registrationKeys.ToArray()))
                .OrderByDescending(h => h.StartedAt)
                .ToListAsync(ct);
        var latestByKey = allHistory
            .GroupBy(h => h.JobKey)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var result = new List<JobOverviewDto>(registry.All.Count);
        foreach (var reg in registry.All)
        {
            configByKey.TryGetValue(reg.Key, out var cfg);
            latestByKey.TryGetValue(reg.Key, out var lastRun);
            result.Add(await BuildOverviewAsync(reg, cfg, lastRun, scheduler, ct));
        }
        return result;
    }

    public async Task<JobOverviewDto?> GetAsync(string key, CancellationToken ct = default)
    {
        var reg = registry.All.FirstOrDefault(r => r.Key == key);
        if (reg is null) return null;

        var cfg = await session.LoadAsync<JobConfig>(key, ct);
        var lastRun = await session.Query<JobRunHistoryEntry>()
            .Where(h => h.JobKey == key)
            .OrderByDescending(h => h.StartedAt)
            .FirstOrDefaultAsync(ct);

        var scheduler = await schedulerFactory.GetScheduler(ct);
        return await BuildOverviewAsync(reg, cfg, lastRun, scheduler, ct);
    }

    public async Task<IReadOnlyList<JobRunHistoryDto>> GetHistoryAsync(string key, int take = 50, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 500) take = 500;

        var entries = await session.Query<JobRunHistoryEntry>()
            .Where(h => h.JobKey == key)
            .OrderByDescending(h => h.StartedAt)
            .Take(take)
            .ToListAsync(ct);
        return entries.Select(ToDto).ToList();
    }

    public async Task UpdateAsync(string key, JobUpdateDto update, CancellationToken ct = default)
    {
        var reg = registry.All.FirstOrDefault(r => r.Key == key)
            ?? throw new InvalidOperationException($"Unknown job key '{key}'");

        var existing = await session.LoadAsync<JobConfig>(key, ct);
        var nextParams = existing?.Parameters;
        if (update.Parameters is not null)
        {
            // Drop unknown keys so a stale UI can't smuggle garbage into the doc.
            var schemaKeys = reg.GetParameterSchema?.Invoke().Select(f => f.Key).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            nextParams = update.Parameters
                .Where(kv => schemaKeys.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        var cfg = (existing ?? new JobConfig { Key = key, Kind = reg.Kind, CreatedAt = DateTime.UtcNow }) with
        {
            CronOverride = update.CronOverride,
            Enabled = update.Enabled ?? existing?.Enabled ?? true,
            Parameters = nextParams,
            UpdatedAt = DateTime.UtcNow,
        };
        session.Store(cfg);
        await session.SaveChangesAsync(ct);

        await RescheduleAsync(reg, cfg, ct);
    }

    public async Task TriggerNowAsync(string key, CancellationToken ct = default)
    {
        var reg = registry.All.FirstOrDefault(r => r.Key == key)
            ?? throw new InvalidOperationException($"Unknown job key '{key}'");

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var jobKey = new JobKey(reg.Key);
        if (!await scheduler.CheckExists(jobKey, ct))
            throw new InvalidOperationException($"Job '{reg.Key}' is not registered with the scheduler");

        // Push a JobDataMap flag so the listener can mark this run as manual.
        var data = new JobDataMap { [JobRunListener.ManualTriggerKey] = true };
        await scheduler.TriggerJob(jobKey, data, ct);
        logger.LogInformation("[Jobs] Manual trigger for {Key}", reg.Key);
    }

    // ── helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Apply the (possibly new) <see cref="JobConfig"/> to Quartz: re-schedule
    /// with the effective cron, or unschedule if disabled.
    /// </summary>
    public async Task RescheduleAsync(JobRegistration reg, JobConfig? cfg, CancellationToken ct = default)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var jobKey = new JobKey(reg.Key);
        var triggerKey = new TriggerKey($"{reg.Key}-trigger");

        // Always make sure the job exists.
        if (!await scheduler.CheckExists(jobKey, ct))
        {
            var jobDetail = JobBuilder.Create(reg.JobType)
                .WithIdentity(jobKey)
                .WithDescription(reg.Description)
                .StoreDurably()
                .Build();
            await scheduler.AddJob(jobDetail, replace: false, ct);
        }

        await scheduler.UnscheduleJob(triggerKey, ct);

        if (cfg is not null && !cfg.Enabled)
        {
            logger.LogInformation("[Jobs] {Key} is disabled — no trigger scheduled", reg.Key);
            return;
        }

        var cron = cfg?.CronOverride ?? reg.DefaultCron;
        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron)
            .Build();
        await scheduler.ScheduleJob(trigger, ct);
        logger.LogInformation("[Jobs] Scheduled {Key} with cron '{Cron}'", reg.Key, cron);
    }

    private static async Task<JobOverviewDto> BuildOverviewAsync(
        JobRegistration reg,
        JobConfig? cfg,
        JobRunHistoryEntry? lastRun,
        IScheduler scheduler,
        CancellationToken ct)
    {
        var triggers = await scheduler.GetTriggersOfJob(new JobKey(reg.Key), ct);
        DateTime? next = triggers
            .Select(t => t.GetNextFireTimeUtc()?.UtcDateTime)
            .Where(d => d.HasValue)
            .OrderBy(d => d)
            .FirstOrDefault();

        var schema = reg.GetParameterSchema?.Invoke() ?? [];
        var parameters = (IReadOnlyDictionary<string, object?>)(cfg?.Parameters ?? new Dictionary<string, object?>());

        return new JobOverviewDto
        {
            Key = reg.Key,
            Name = cfg?.DisplayName ?? reg.Name,
            Description = cfg?.Description ?? reg.Description,
            Kind = reg.Kind.ToString(),
            EffectiveCron = cfg?.CronOverride ?? reg.DefaultCron,
            DefaultCron = reg.DefaultCron,
            HasOverride = !string.IsNullOrWhiteSpace(cfg?.CronOverride),
            Enabled = cfg?.Enabled ?? true,
            NextFireAt = next,
            LastRun = lastRun is null ? null : ToDto(lastRun),
            ParameterSchema = schema,
            Parameters = parameters,
        };
    }

    private static JobRunHistoryDto ToDto(JobRunHistoryEntry h) => new()
    {
        Id = h.Id,
        JobKey = h.JobKey,
        StartedAt = h.StartedAt,
        FinishedAt = h.FinishedAt,
        DurationMs = h.DurationMs,
        Success = h.Success,
        ErrorMessage = h.ErrorMessage,
        ExceptionDetail = h.ExceptionDetail,
        ResultSummary = h.ResultSummary,
        ManualTrigger = h.ManualTrigger,
    };
}
