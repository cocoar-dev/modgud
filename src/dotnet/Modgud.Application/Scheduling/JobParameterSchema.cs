namespace Modgud.Application.Scheduling;

/// <summary>
/// One configurable parameter on a job. Returned by <see cref="IJobsService"/>
/// as part of <see cref="JobOverviewDto.ParameterSchema"/>. Drives the
/// generic settings form in the admin UI.
///
/// <para>Job authors describe their configurable inputs declaratively so
/// admins can tune the job without touching code. Values are stored under
/// <see cref="Infrastructure.Scheduling.JobConfig.Parameters"/> as an opaque
/// JSON dictionary keyed by <see cref="Key"/>.</para>
/// </summary>
public sealed record JobParameterField
{
    /// <summary>Stable parameter identifier (used as the key in <c>Parameters</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable label rendered next to the input.</summary>
    public required string Label { get; init; }

    /// <summary>Field type. UI renders an appropriate input.</summary>
    public required JobParameterType Type { get; init; }

    /// <summary>Default applied when the user clears the field. Typed per <see cref="Type"/>.</summary>
    public object? Default { get; init; }

    /// <summary>Optional help text shown under the input.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional grouping. Fields sharing a section are rendered together
    /// under a heading.
    /// </summary>
    public string? Section { get; init; }

    /// <summary>
    /// Placeholder shown when the value is empty. Use it to communicate
    /// fallback semantics (e.g. "leave blank for unlimited").
    /// </summary>
    public string? Placeholder { get; init; }
}

public enum JobParameterType
{
    Number = 0,
    String = 1,
    Boolean = 2,
}
