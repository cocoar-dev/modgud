using JasperFx.Events.Daemon;

namespace Modgud.Infrastructure.Cluster;

/// <summary>
/// How this host coordinates background work with its peers (ADR 0010).
/// <para>
/// <see cref="WolverineManaged"/> is the Production shape: Wolverine runs in
/// <c>Balanced</c> durability mode, distributes every Marten async projection and
/// event subscription across the live nodes, and Quartz runs on a clustered
/// Postgres job store. It is used with one instance as well as with two, so the
/// single-instance operator runs exactly the code the two-instance operator runs.
/// </para>
/// <para>
/// <see cref="SingleProcess"/> keeps Marten's own daemon and the in-memory
/// Quartz store; it exists for Development and Testing, where the host is
/// restarted constantly and the integration suite drives projections through
/// explicit interactive daemons.
/// </para>
/// </summary>
public enum ClusterCoordination
{
    /// <summary>Marten daemon in-process (mode chosen by the host), Wolverine Solo, Quartz in memory.</summary>
    SingleProcess,

    /// <summary>Wolverine Balanced + managed projection distribution, Quartz clustered on Postgres.</summary>
    WolverineManaged,
}

/// <summary>
/// Host-level decisions the infrastructure wiring needs. Built once in
/// <c>Program.cs</c> from the hosting environment.
/// </summary>
public sealed record ClusterHostingOptions
{
    public required ClusterCoordination Coordination { get; init; }

    /// <summary>
    /// Marten daemon mode when <see cref="Coordination"/> is
    /// <see cref="ClusterCoordination.SingleProcess"/>. Ignored under Wolverine-managed
    /// distribution, where Wolverine sets the daemon to externally managed itself.
    /// </summary>
    public DaemonMode SingleProcessDaemonMode { get; init; } = DaemonMode.Solo;

    /// <summary>
    /// A stable, human-readable name for this node in logs and health output.
    /// Defaults to the machine (container) name.
    /// </summary>
    public string NodeName { get; init; } = Environment.MachineName;

    /// <summary>
    /// The SignalR backplane and data-event relay transport this host runs with.
    /// Decided by the host from environment and settings; read by readiness.
    /// </summary>
    public ClusterBackplane Backplane { get; init; } = ClusterBackplane.None;

    public bool IsWolverineManaged => Coordination == ClusterCoordination.WolverineManaged;
}

/// <summary>Transport for cross-node SignalR routing and data-event relay (ADR 0010, D5).</summary>
public enum ClusterBackplane
{
    /// <summary>Single-process host; pushes stay in this process.</summary>
    None,

    /// <summary>LISTEN/NOTIFY on the master database — the Production default.</summary>
    Postgres,

    /// <summary>Valkey/Redis, for high realtime volume or when Redis is in the stack anyway.</summary>
    Redis,
}
