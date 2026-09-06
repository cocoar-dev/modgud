using Marten;
using Microsoft.Extensions.Logging;
using Quartz;
using Modgud.Application.Scheduling;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.Scheduling;

/// <inheritdoc />
/// <remarks>
/// Pulls registrations from <see cref="IJobRegistry"/>, overrides from
/// Marten (<see cref="JobConfig"/>), live schedule state from Quartz, and
/// run history from Marten. The single non-trivial bit is the cron-reschedule
/// path — we delete and recreate the trigger to keep semantics simple.
/// </remarks>
internal sealed class JobsService(
    IJobRegistry registry,
    ISchedulerFactory schedulerFactory,
    RealmJobScheduler jobScheduler,
    IGlobalStore globalStore,
    IDocumentSession session,
    ILogger<JobsService> logger) : IJobsService
{
    public async Task<IReadOnlyList<JobOverviewDto>> GetAllAsync(CancellationToken ct = default)
    {
        var realm = await GetCurrentRealmAsync(ct);
        var registrations = VisibleRegistrations(realm.IsControlPlane).ToList();
        var realmKeys = registrations
            .Where(r => r.Scope == JobScope.Realm)
            .Select(r => r.Key)
            .ToArray();
        var systemKeys = registrations
            .Where(r => r.Scope == JobScope.System)
            .Select(r => r.Key)
            .ToArray();

        var (configs, allHistory) = await LoadStateAsync(session, realmKeys, ct);
        if (systemKeys.Length > 0)
        {
            await using var systemSession = globalStore.QuerySession();
            var (systemConfigs, systemHistory) = await LoadStateAsync(
                systemSession, systemKeys, ct);
            configs.AddRange(systemConfigs);
            allHistory.AddRange(systemHistory);
        }

        var configByKey = configs.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var latestByKey = allHistory
            .GroupBy(h => h.JobKey)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(h => h.StartedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var result = new List<JobOverviewDto>(registrations.Count);
        foreach (var reg in registrations)
        {
            configByKey.TryGetValue(reg.Key, out var cfg);
            latestByKey.TryGetValue(reg.Key, out var lastRun);
            result.Add(await BuildOverviewAsync(reg, realm.Slug, cfg, lastRun, scheduler, ct));
        }
        return result;
    }

    public async Task<JobOverviewDto?> GetAsync(string key, CancellationToken ct = default)
    {
        var realm = await GetCurrentRealmAsync(ct);
        var reg = VisibleRegistrations(realm.IsControlPlane)
            .FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (reg is null) return null;

        JobConfig? cfg;
        JobRunHistoryEntry? lastRun;
        if (reg.Scope == JobScope.System)
        {
            await using var systemSession = globalStore.QuerySession();
            cfg = await systemSession.LoadAsync<JobConfig>(reg.Key, ct);
            lastRun = await GetLastRunAsync(systemSession, reg.Key, ct);
        }
        else
        {
            cfg = await session.LoadAsync<JobConfig>(reg.Key, ct);
            lastRun = await GetLastRunAsync(session, reg.Key, ct);
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        return await BuildOverviewAsync(reg, realm.Slug, cfg, lastRun, scheduler, ct);
    }

    public async Task<IReadOnlyList<JobRunHistoryDto>> GetHistoryAsync(string key, int take = 50, CancellationToken ct = default)
    {
        var realm = await GetCurrentRealmAsync(ct);
        var reg = VisibleRegistrations(realm.IsControlPlane)
            .FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown job key '{key}'");

        if (take < 1) take = 1;
        if (take > 500) take = 500;

        List<JobRunHistoryEntry> entries;
        if (reg.Scope == JobScope.System)
        {
            await using var systemSession = globalStore.QuerySession();
            entries = await GetHistoryAsync(systemSession, reg.Key, take, ct);
        }
        else
        {
            entries = await GetHistoryAsync(session, reg.Key, take, ct);
        }

        return entries.Select(ToDto).ToList();
    }

    public async Task UpdateAsync(string key, JobUpdateDto update, CancellationToken ct = default)
    {
        var realm = await GetCurrentRealmAsync(ct);
        var reg = VisibleRegistrations(realm.IsControlPlane)
            .FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown job key '{key}'");

        JobConfig cfg;
        if (reg.Scope == JobScope.System)
        {
            await using var systemSession = globalStore.LightweightSession();
            var existing = await systemSession.LoadAsync<JobConfig>(reg.Key, ct);
            cfg = BuildConfig(reg, update, existing);
            systemSession.Store(cfg);
            await systemSession.SaveChangesAsync(ct);
        }
        else
        {
            var existing = await session.LoadAsync<JobConfig>(reg.Key, ct);
            cfg = BuildConfig(reg, update, existing);
            session.Store(cfg);
            await session.SaveChangesAsync(ct);
        }

        await jobScheduler.ApplyAsync(reg, realm.Slug, cfg, ct);
    }

    public async Task TriggerNowAsync(string key, Guid? triggeredByUserId = null, CancellationToken ct = default)
    {
        var realm = await GetCurrentRealmAsync(ct);
        var reg = VisibleRegistrations(realm.IsControlPlane)
            .FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown job key '{key}'");

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var jobKey = RealmJobScheduler.GetJobKey(reg, realm.Slug);
        if (!await scheduler.CheckExists(jobKey, ct))
            throw new InvalidOperationException($"Job '{reg.Key}' is not registered with the scheduler");

        // Push the manual-trigger flag + (optional) triggering-user id into
        // the JobDataMap. JobRunListener reads them back on completion and
        // stamps the history entry; the job→inbox bridge then routes the
        // ManualJobCompleted notification to the triggering user.
        // String values only: the clustered job store persists the map as
        // name/value properties (ADR 0022), and JobRunListener parses them back.
        var data = new JobDataMap { [JobRunListener.ManualTriggerKey] = bool.TrueString };
        if (triggeredByUserId is Guid uid && uid != Guid.Empty)
            data[JobRunListener.TriggeredByUserIdKey] = uid.ToString("D");
        await scheduler.TriggerJob(jobKey, data, ct);
        logger.LogInformation(
            "[Jobs] Manual trigger for {Key} in realm {Realm} by user {UserId}",
            reg.Key, realm.Slug, triggeredByUserId?.ToString() ?? "(unknown)");
    }

    // ── helpers ─────────────────────────────────────────────────────

    private IEnumerable<JobRegistration> VisibleRegistrations(bool isControlPlane)
    {
        return registry.All.Where(r =>
            r.Scope == JobScope.Realm
            || (r.Scope == JobScope.System && isControlPlane));
    }

    private async Task<Realm> GetCurrentRealmAsync(CancellationToken ct)
    {
        var slug = TenantContext.Current;
        await using var globalSession = globalStore.QuerySession();
        return await globalSession.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct)
            ?? throw new InvalidOperationException($"Unknown current realm '{slug}'");
    }

    private static async Task<(List<JobConfig> Configs, List<JobRunHistoryEntry> History)> LoadStateAsync(
        IQuerySession source,
        string[] keys,
        CancellationToken ct)
    {
        if (keys.Length == 0)
            return ([], []);

        var configs = await source.Query<JobConfig>()
            .Where(c => c.Key.IsOneOf(keys))
            .ToListAsync(ct);
        var history = await source.Query<JobRunHistoryEntry>()
            .Where(h => h.JobKey.IsOneOf(keys))
            .ToListAsync(ct);
        return (configs.ToList(), history.ToList());
    }

    private static Task<JobRunHistoryEntry?> GetLastRunAsync(
        IQuerySession source,
        string key,
        CancellationToken ct) =>
        source.Query<JobRunHistoryEntry>()
            .Where(h => h.JobKey == key)
            .OrderByDescending(h => h.StartedAt)
            .FirstOrDefaultAsync(ct);

    private static async Task<List<JobRunHistoryEntry>> GetHistoryAsync(
        IQuerySession source,
        string key,
        int take,
        CancellationToken ct)
    {
        var entries = await source.Query<JobRunHistoryEntry>()
            .Where(h => h.JobKey == key)
            .OrderByDescending(h => h.StartedAt)
            .Take(take)
            .ToListAsync(ct);
        return entries.ToList();
    }

    private static JobConfig BuildConfig(
        JobRegistration registration,
        JobUpdateDto update,
        JobConfig? existing)
    {
        var nextParams = existing?.Parameters;
        if (update.Parameters is not null)
        {
            // Drop unknown keys so a stale UI can't smuggle garbage into the doc.
            var schemaKeys = registration.GetParameterSchema?.Invoke()
                .Select(f => f.Key)
                .ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            nextParams = update.Parameters
                .Where(kv => schemaKeys.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        return (existing ?? new JobConfig
        {
            Key = registration.Key,
            Kind = registration.Kind,
            CreatedAt = DateTime.UtcNow,
        }) with
        {
            // v2 merge-patch: absent = keep the override, explicit null (or a
            // blank string) = clear back to the default cron, value = set.
            CronOverride = !update.CronOverride.HasValue
                ? existing?.CronOverride
                : string.IsNullOrWhiteSpace(update.CronOverride.Value) ? null : update.CronOverride.Value,
            Enabled = update.Enabled ?? existing?.Enabled ?? true,
            Parameters = nextParams,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static async Task<JobOverviewDto> BuildOverviewAsync(
        JobRegistration reg,
        string realmSlug,
        JobConfig? cfg,
        JobRunHistoryEntry? lastRun,
        IScheduler scheduler,
        CancellationToken ct)
    {
        var triggers = await scheduler.GetTriggersOfJob(
            RealmJobScheduler.GetJobKey(reg, realmSlug), ct);
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
            Scope = reg.Scope.ToString(),
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
