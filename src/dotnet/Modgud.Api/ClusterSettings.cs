namespace Modgud.Api;

/// <summary>
/// Deployment-wide settings for running more than one Modgud instance against
/// one database (ADR 0010). Bound from configuration JSON (section "Cluster")
/// with env overrides: <c>Cluster__DrainDelaySeconds</c>, <c>Cluster__NodeName</c>.
/// <para>
/// There is deliberately no instance count and no transport choice here.
/// Production always runs the cluster-capable code path, with the cross-node
/// live-update relay on the master database; how many nodes are alive is read
/// from Wolverine's node table at runtime.
/// </para>
/// </summary>
public class ClusterSettings
{
    /// <summary>
    /// Human-readable name of this node in logs, health output and the admin
    /// UI. Defaults to the machine (container) name.
    /// </summary>
    public string? NodeName { get; set; }

    /// <summary>
    /// Seconds to keep serving after SIGTERM with readiness already reporting
    /// 503, so the reverse proxy takes this node out of rotation before Kestrel
    /// stops accepting connections. 0 disables the drain. Only meaningful in
    /// Production; Development and Testing always use 0.
    /// </summary>
    public int DrainDelaySeconds { get; set; } = 5;
}
