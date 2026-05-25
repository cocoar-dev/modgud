namespace Modgud.Infrastructure.Events;

/// <summary>
/// Guard for projection side effects (SignalR dispatch via Wolverine).
/// Must be enabled after Wolverine has started to avoid
/// WolverineHasNotStartedException during daemon catchup on startup.
/// </summary>
public static class ProjectionSideEffects
{
    public static bool Enabled { get; set; }
}
