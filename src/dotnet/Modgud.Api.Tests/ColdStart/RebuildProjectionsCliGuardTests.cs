using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Cluster;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// FR-CO-01 — the CLI rebuild cannot pause another running API instance's projection
/// processing the way the HTTP rebuild pauses its own, so it must not run blind while one
/// is live. It refuses when other live instances are known (overridable with --force) and
/// only warns when the topology cannot be read, so the break-glass path stays usable.
/// </summary>
public class RebuildProjectionsCliGuardTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    private static readonly ClusterNodeInfo Self = new(
        Guid.NewGuid(), 0, "cli", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsLeader: false, IsSelf: true);

    private static readonly ClusterNodeInfo RunningApi = new(
        Guid.NewGuid(), 1, "modgud-api-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsLeader: true, IsSelf: false);

    [Fact]
    public async Task A_rebuild_refuses_while_another_instance_is_live()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(
            new ClusterNodesOverride(host.Services, new FakeNodes([Self, RunningApi])),
            "rebuild-projections");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("modgud-api-1", result.StdErr);
        Assert.DoesNotContain("ViewProjections", result.StdOut);
    }

    [Fact]
    public async Task Force_runs_the_rebuild_despite_a_live_instance()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(
            new ClusterNodesOverride(host.Services, new FakeNodes([Self, RunningApi])),
            "rebuild-projections", "--force");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ViewProjections", result.StdOut);
    }

    [Fact]
    public async Task An_unreadable_topology_only_warns()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(
            new ClusterNodesOverride(host.Services, new FakeNodes(null)),
            "rebuild-projections");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ViewProjections", result.StdOut);
        Assert.Contains("could not", result.StdOut + result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeNodes(IReadOnlyList<ClusterNodeInfo>? nodes) : IClusterNodes
    {
        public Guid LocalNodeId => Self.NodeId;
        public string LocalNodeName => Self.Description;
        public Task<IReadOnlyList<ClusterNodeInfo>> GetLiveNodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ClusterNodeInfo>>(nodes ?? [Self]);
        public Task<IReadOnlyList<ClusterNodeInfo>?> TryGetLiveNodesAsync(CancellationToken ct = default)
            => Task.FromResult(nodes);
    }

    /// <summary>Serves a fake <see cref="IClusterNodes"/> from the root provider and from
    /// every scope the CLI creates; everything else comes from the real host.</summary>
    private sealed class ClusterNodesOverride(IServiceProvider inner, IClusterNodes nodes)
        : IServiceProvider, IServiceScopeFactory
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IClusterNodes) ? nodes
            : serviceType == typeof(IServiceScopeFactory) ? this
            : inner.GetService(serviceType);

        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), nodes);

        private sealed class Scope(IServiceScope scope, IClusterNodes nodes) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new ClusterNodesOverride(scope.ServiceProvider, nodes);
            public void Dispose() => scope.Dispose();
        }
    }
}
