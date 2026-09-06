using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modgud.Api.Cluster;
using Modgud.Infrastructure.Cluster;

namespace Modgud.Api.HealthChecks;

/// <summary>
/// Readiness facts that only exist once a deployment can have more than one
/// node (ADR 0022):
/// <list type="bullet">
///   <item>the node is draining after SIGTERM → 503 so the proxy stops routing here;</item>
///   <item>more than one node is alive but this host runs without the
///   cross-node live-update relay (only possible outside Production) → 503
///   with a message that names the fix. Pushes raised on one node would never
///   reach connections on the other, which is a functional break, not a
///   warning.</item>
/// </list>
/// The live-node count comes from Wolverine's node table, never from configuration.
/// </summary>
public sealed class ClusterHealthCheck(
    IClusterNodes nodes,
    ShutdownState shutdown,
    ClusterHostingOptions hosting) : IHealthCheck
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
            ["relay"] = hosting.CrossNodeRelay ? "signalarrr-postgres" : "none",
        };

        if (live.Count > 1 && !hosting.CrossNodeRelay)
        {
            return HealthCheckResult.Unhealthy(
                $"{live.Count} nodes are alive but this host runs without the cross-node live-update relay. " +
                "Run every node with ASPNETCORE_ENVIRONMENT=Production (see docs/operate/deployment, " +
                "'Running two instances') or scale back to one instance.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            live.Count == 1 ? "Single live node." : $"{live.Count} live nodes, backplane and live-update relay active.",
            data);
    }
}
