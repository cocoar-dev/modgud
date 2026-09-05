using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modgud.Api.Cluster;
using Modgud.Api.HealthChecks;
using Modgud.Infrastructure.Cluster;

namespace Modgud.Tests.Unit.Cluster;

/// <summary>
/// Readiness facts of ADR 0010: a draining node and "two nodes without the
/// relay" both refuse traffic; one node, or two with the relay, are ready.
/// </summary>
public class ClusterHealthCheckTests
{
    [Fact]
    public async Task Single_node_without_relay_is_healthy()
    {
        var sut = new ClusterHealthCheck(Nodes(1), new ShutdownState(), Hosting(relay: false));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, result.Data["liveNodes"]);
    }

    [Fact]
    public async Task Two_nodes_without_relay_are_unhealthy_and_name_the_fix()
    {
        var sut = new ClusterHealthCheck(Nodes(2), new ShutdownState(), Hosting(relay: false));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("without the cross-node live-update relay", result.Description);
    }

    [Fact]
    public async Task Two_nodes_with_relay_are_healthy()
    {
        var sut = new ClusterHealthCheck(Nodes(2), new ShutdownState(), Hosting(relay: true));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Draining_node_is_unhealthy_regardless_of_cluster_state()
    {
        var shutdown = new ShutdownState();
        shutdown.MarkStopping();
        var sut = new ClusterHealthCheck(Nodes(1), shutdown, Hosting(relay: true));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Draining", result.Description);
    }

    private static ClusterHostingOptions Hosting(bool relay) => new()
    {
        Coordination = ClusterCoordination.WolverineManaged,
        CrossNodeRelay = relay,
    };

    private static IClusterNodes Nodes(int count) => new FakeNodes(count);

    private sealed class FakeNodes(int count) : IClusterNodes
    {
        public Guid LocalNodeId { get; } = Guid.NewGuid();
        public string LocalNodeName => "test-node";

        public Task<IReadOnlyList<ClusterNodeInfo>> GetLiveNodesAsync(CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = Enumerable.Range(1, count)
                .Select(i => new ClusterNodeInfo(
                    i == 1 ? LocalNodeId : Guid.NewGuid(), i, $"node-{i}", now, now, IsLeader: i == 1, IsSelf: i == 1))
                .ToList();
            return Task.FromResult<IReadOnlyList<ClusterNodeInfo>>(nodes);
        }
    }
}
