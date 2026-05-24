namespace Cocoar.Auth.Application.Scheduling;

/// <summary>
/// Admin-facing facade combining the static job registry (compiled jobs),
/// the persisted <c>JobConfig</c> overrides, the running Quartz scheduler,
/// and the <c>JobRunHistoryEntry</c> ledger. Used by the admin endpoints
/// at <c>/api/admin/jobs</c>; not consumed from the request path elsewhere.
/// </summary>
public interface IJobsService
{
    Task<IReadOnlyList<JobOverviewDto>> GetAllAsync(CancellationToken ct = default);
    Task<JobOverviewDto?> GetAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<JobRunHistoryDto>> GetHistoryAsync(string key, int take = 50, CancellationToken ct = default);

    /// <summary>
    /// Update schedule and/or enabled state. Reschedules the trigger in
    /// Quartz immediately.
    /// </summary>
    Task UpdateAsync(string key, JobUpdateDto update, CancellationToken ct = default);

    /// <summary>
    /// Fire the job once, off-schedule. Result is logged to history with
    /// <c>ManualTrigger = true</c>.
    /// </summary>
    Task TriggerNowAsync(string key, CancellationToken ct = default);
}

public sealed record JobOverviewDto
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Kind { get; init; }                 // "System" | "Script"
    public required string EffectiveCron { get; init; }        // override if present, else default
    public required string DefaultCron { get; init; }
    public bool HasOverride { get; init; }
    public bool Enabled { get; init; }
    public DateTime? NextFireAt { get; init; }
    public JobRunHistoryDto? LastRun { get; init; }

    /// <summary>
    /// Declarative description of the job's configurable inputs. Empty when
    /// the job has no tunable parameters. Drives the dynamic settings form.
    /// </summary>
    public IReadOnlyList<JobParameterField> ParameterSchema { get; init; } = [];

    /// <summary>
    /// Current persisted parameter values keyed by <see cref="JobParameterField.Key"/>.
    /// Missing keys mean "use the schema's Default". Values are JSON-typed
    /// to match each field's <see cref="JobParameterType"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}

public sealed record JobRunHistoryDto
{
    public required Guid Id { get; init; }
    public required string JobKey { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ExceptionDetail { get; init; }
    public string? ResultSummary { get; init; }
    public bool ManualTrigger { get; init; }
}

public sealed record JobUpdateDto
{
    /// <summary>
    /// <c>null</c> = use the registration's default cron (clears override).
    /// Non-null = upsert override with this cron expression.
    /// </summary>
    public string? CronOverride { get; init; }
    public bool? Enabled { get; init; }

    /// <summary>
    /// When non-null, replaces the persisted parameters wholesale. Keys not
    /// in the registration's schema are dropped; missing keys fall back to
    /// the schema's <c>Default</c> at read time.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }
}
