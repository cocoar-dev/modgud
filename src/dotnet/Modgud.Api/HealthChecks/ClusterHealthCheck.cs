using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modgud.Api.Cluster;
using Modgud.Infrastructure.Cluster;

namespace Modgud.Api.HealthChecks;

/// <summary>
/// Readiness facts that only exist once a deployment can have more than one
/// node (ADR 0010):
/// <list type="bullet">
///   <item>the node is draining after SIGTERM → 503 so the proxy stops routing here;</item>
///   <item>more than one node is alive but no SignalR backplane is configured →
///   503 with a message that names the fix. Pushes raised on one node would never
///   reach connections on the other, which is a functional break, not a warning.</item>
/// </list>
/// The live-node count comes from Wolverine's node table, never from configuration.
/// </summary>
public sealed class ClusterHealthCheck(
    IClusterNodes nodes,
    ShutdownState shutdown,
    ClusterSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (shutdown.IsStopping)
            return HealthCheckResult.Unhealthy("Draining — this node is shutting down.");

        var live = await nodes.GetLiveNodesAsync(cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["node"] = nodes.LocalNodeName,
            ["liveNodes"] = live.Count,
            ["backplane"] = settings.Backplane.IsConfigured,
        };

        if (live.Count > 1 && !settings.Backplane.IsConfigured)
        {
            return HealthCheckResult.Unhealthy(
                $"{live.Count} nodes are alive but no SignalR backplane is configured. " +
                "Set Cluster__Backplane__ConnectionString on every node (see docs/operate/deployment, " +
                "'Running two instances') or scale back to one instance.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            live.Count == 1 ? "Single live node." : $"{live.Count} live nodes, backplane active.",
            data);
    }
}
