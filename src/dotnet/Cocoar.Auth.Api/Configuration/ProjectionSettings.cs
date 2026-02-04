using Cocoar.Auth.Infrastructure.Interfaces;

namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// Marten projection configuration settings.
/// </summary>
public class ProjectionSettings: IProjectionSettings
{
    /// <summary>
    /// Whether to use async projections with the async daemon.
    /// Default is true for production. Set to false for tests to avoid daemon locking issues.
    /// </summary>
    public bool UseAsyncProjections { get; init; } = true;
}
