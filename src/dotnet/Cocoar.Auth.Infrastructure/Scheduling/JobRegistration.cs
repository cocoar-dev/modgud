using Quartz;
using Cocoar.Auth.Application.Scheduling;

namespace Cocoar.Auth.Infrastructure.Scheduling;

/// <summary>
/// Compile-time description of a system job. Registered via
/// <c>AddSystemJob&lt;TJob&gt;(...)</c> at startup. The registry walks all
/// registrations, applies any matching <see cref="JobConfig"/> overrides
/// from Marten, and schedules them in Quartz.
/// </summary>
public sealed record JobRegistration
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Quartz cron expression (7 fields). Used when no <see cref="JobConfig"/> override exists.</summary>
    public required string DefaultCron { get; init; }
    public JobKind Kind { get; init; } = JobKind.System;
    /// <summary>The compiled job type (must implement <see cref="IJob"/>). Required for System jobs.</summary>
    public required Type JobType { get; init; }

    /// <summary>
    /// Optional factory returning the job's configurable inputs. The job is
    /// re-asked on every overview load so a schema derived from a runtime
    /// registry stays current without a restart. <c>null</c> = no
    /// configurable inputs.
    /// </summary>
    public Func<IReadOnlyList<JobParameterField>>? GetParameterSchema { get; init; }
}
