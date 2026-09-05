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
    /// Whether this host runs the SignalARRR backplane on the master database and
    /// relays data events to its peers over it (ADR 0010, D5). True in
    /// Production; a single-process host keeps pushes in-process. Read by
    /// readiness: two live nodes without the relay is a functional break.
    /// </summary>
    public bool CrossNodeRelay { get; init; }

    public bool IsWolverineManaged => Coordination == ClusterCoordination.WolverineManaged;
}
