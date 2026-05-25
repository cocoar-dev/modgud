using JasperFx;
using Marten.Schema;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// One execution record per job run. Written by <see cref="JobRunListener"/>
/// after the job's <c>Execute</c> returns (success or fail). Append-only —
/// admin UI shows the last N entries for each job key.
/// </summary>
[DocumentAlias("job_run_history")]
public record JobRunHistoryEntry
{
    [Identity]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Matches <see cref="JobConfig.Key"/>.</summary>
    public string JobKey { get; init; } = string.Empty;

    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }

    /// <summary>Duration in milliseconds (FinishedAt - StartedAt).</summary>
    public long DurationMs { get; init; }

    public bool Success { get; init; }

    /// <summary>
    /// <c>null</c> on success; first-line summary on failure (full stack
    /// trace goes to <see cref="ExceptionDetail"/>).
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Full exception ToString() — useful for triage.</summary>
    public string? ExceptionDetail { get; init; }

    /// <summary>
    /// Optional freeform result message — system jobs can put a one-line
    /// summary here (e.g. "Dismissed 42 stale items"). Surfaces in the
    /// admin UI's run history.
    /// </summary>
    public string? ResultSummary { get; init; }

    /// <summary>
    /// Whether this run was a manual trigger (vs scheduled). Useful so the
    /// UI can mark admin-triggered runs distinctly and so future job→inbox
    /// notifications can route the completion notice to the triggering user.
    /// </summary>
    public bool ManualTrigger { get; init; }

    /// <summary>
    /// User id of the admin who manually triggered this run, if any. Set
    /// only when <see cref="ManualTrigger"/> is true and the trigger path
    /// captured the caller's identity (HTTP-triggered runs do, internal
    /// re-triggers don't).
    /// </summary>
    public Guid? TriggeredByUserId { get; init; }
}
