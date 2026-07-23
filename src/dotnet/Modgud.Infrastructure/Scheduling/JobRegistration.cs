using Quartz;
using Modgud.Application.Scheduling;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Defines who owns a scheduled job.
/// </summary>
public enum JobScope
{
    /// <summary>
    /// One independent Quartz job + trigger per realm. Configuration and run
    /// history live in that realm's tenant database.
    /// </summary>
    Realm,

    /// <summary>
    /// One deployment-wide Quartz job. It is visible and configurable only
    /// from the realm that currently holds the Control-Plane role.
    /// </summary>
    System,
}

/// <summary>
/// Compile-time description of a compiled job. Registered via
/// <c>AddRealmJob&lt;TJob&gt;(...)</c> or <c>AddSystemJob&lt;TJob&gt;(...)</c>
/// at startup. The registry applies the owning realm's matching
/// <see cref="JobConfig"/> and schedules the appropriate Quartz instance(s).
/// </summary>
public sealed record JobRegistration
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Quartz cron expression (7 fields). Used when no <see cref="JobConfig"/> override exists.</summary>
    public required string DefaultCron { get; init; }
    public JobKind Kind { get; init; } = JobKind.System;
    /// <summary>The compiled job type (must implement <see cref="IJob"/>).</summary>
    public required Type JobType { get; init; }
    public required JobScope Scope { get; init; }

    /// <summary>
    /// Realm jobs normally stop when their realm is deactivated. Set this only
    /// for tenant-owned hygiene that must continue while a soft-deleted realm's
    /// database still exists (for example expired private-key cleanup).
    /// Ignored for <see cref="JobScope.System"/>.
    /// </summary>
    public bool RunWhenRealmInactive { get; init; }

    /// <summary>
    /// Optional factory returning the job's configurable inputs. The job is
    /// re-asked on every overview load so a schema derived from a runtime
    /// registry stays current without a restart. <c>null</c> = no
    /// configurable inputs.
    /// </summary>
    public Func<IReadOnlyList<JobParameterField>>? GetParameterSchema { get; init; }
}
