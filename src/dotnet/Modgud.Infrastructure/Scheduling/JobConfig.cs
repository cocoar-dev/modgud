using JasperFx;
using Marten.Schema;
using Modgud.Application.Scheduling;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Runtime override for a job's schedule + enabled state. Marten document
/// keyed by <see cref="Key"/> — system jobs auto-register on startup with
/// defaults and look up a matching JobConfig to apply overrides.
///
/// Phase 1 only persists overrides for system jobs. The <see cref="ScriptSource"/>
/// slot is reserved for future JsEval-authored jobs (Modules) — kept on the
/// same document so the storage shape doesn't change later.
/// </summary>
[DocumentAlias("job_config")]
public record JobConfig
{
    /// <summary>
    /// Job identifier — matches the <c>Key</c> a system job registered with,
    /// or the unique identifier of a future script job.
    /// </summary>
    [Identity]
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Kind of job. System = compiled handler, Script = JsEval (future).
    /// </summary>
    public JobKind Kind { get; init; } = JobKind.System;

    /// <summary>
    /// Cron expression. <c>null</c> = use the system job's default. Quartz
    /// cron format (7 fields: sec min hour day-of-month month day-of-week year).
    /// </summary>
    public string? CronOverride { get; init; }

    /// <summary>
    /// When false the job is unscheduled — survives restart, won't trigger.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Reserved for future JsEval module-based script body. Unused while
    /// Kind == System.
    /// </summary>
    public string? ScriptSource { get; init; }

    /// <summary>
    /// Display name (script jobs need a user-set name; system jobs default
    /// to the registration's name and ignore this).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Free-form admin description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Tunable parameters keyed by <c>JobParameterField.Key</c>. Stored as a
    /// plain dictionary so STJ round-trips it cleanly; the values are typed
    /// per the registration's schema and validated on update. <c>null</c>
    /// when the admin never customised the job.
    /// </summary>
    public Dictionary<string, object?>? Parameters { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; init; }
}
