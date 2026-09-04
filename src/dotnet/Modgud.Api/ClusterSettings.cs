namespace Modgud.Api;

/// <summary>
/// Deployment-wide settings for running more than one Modgud instance against
/// one database (ADR 0010). Bound from configuration JSON (section "Cluster")
/// with env overrides: <c>Cluster__Backplane__ConnectionString</c>,
/// <c>Cluster__DrainDelaySeconds</c>, <c>Cluster__NodeName</c>.
/// <para>
/// There is deliberately no instance count here. Production always runs the
/// cluster-capable code path; how many nodes are alive is read from Wolverine's
/// node table at runtime, and a second node without a backplane is reported by
/// the readiness probe instead of being guessed from configuration.
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

    public BackplaneSettings Backplane { get; set; } = new();

    public class BackplaneSettings
    {
        /// <summary>
        /// StackExchange.Redis connection string of the Valkey/Redis instance
        /// the SignalARRR backplane runs on, e.g. <c>valkey:6379,abortConnect=false</c>.
        /// Empty = no backplane = single-node SignalR (fine for one instance;
        /// readiness fails as soon as a second node shows up).
        /// </summary>
        public string ConnectionString { get; set; } = "";

        /// <summary>
        /// Channel/key prefix so several deployments can share one Valkey.
        /// </summary>
        public string ChannelPrefix { get; set; } = "modgud";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
    }
}
