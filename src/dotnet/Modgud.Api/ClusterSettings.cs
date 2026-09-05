namespace Modgud.Api;

/// <summary>
/// Deployment-wide settings for running more than one Modgud instance against
/// one database (ADR 0010). Bound from configuration JSON (section "Cluster")
/// with env overrides: <c>Cluster__Backplane__Provider</c>,
/// <c>Cluster__Backplane__ConnectionString</c>, <c>Cluster__DrainDelaySeconds</c>,
/// <c>Cluster__NodeName</c>.
/// <para>
/// There is deliberately no instance count here. Production always runs the
/// cluster-capable code path, with the SignalR backplane and the cross-node
/// event relay on the master database by default; how many nodes are alive is
/// read from Wolverine's node table at runtime.
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
        /// <c>Postgres</c> (default): the SignalARRR backplane and the data-event
        /// relay run on the master database — nothing to configure, no second
        /// stateful service. <c>Redis</c>: a Valkey/Redis instance instead, for
        /// deployments with a high realtime volume or Redis already in the stack;
        /// needs <see cref="ConnectionString"/>.
        /// </summary>
        public string Provider { get; set; } = "Postgres";

        /// <summary>
        /// StackExchange.Redis connection string, only with
        /// <see cref="Provider"/> = <c>Redis</c>, e.g. <c>valkey:6379,abortConnect=false</c>.
        /// </summary>
        public string ConnectionString { get; set; } = "";

        /// <summary>
        /// Redis channel/key prefix so several deployments can share one Valkey.
        /// </summary>
        public string ChannelPrefix { get; set; } = "modgud";

        /// <summary>
        /// Postgres schema of the SignalARRR backplane tables and notification
        /// channels in the master database. Change it only when two deployments
        /// share one master database, which they should not.
        /// </summary>
        public string Schema { get; set; } = "signalarrr";

        public bool UsesRedis => string.Equals(Provider, "Redis", StringComparison.OrdinalIgnoreCase);
    }
}
