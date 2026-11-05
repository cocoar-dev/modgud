using Microsoft.AspNetCore.Http;
using Modgud.Api.Features.Admin;
using Modgud.Infrastructure.Cluster;

namespace Modgud.Tests.Unit.Api.Features.Admin;

/// <summary>
/// B9-CL01 — the rebuild needs proof of a single running instance. An unreadable node table
/// used to come back as "just me" and let the rebuild run next to other nodes' projection
/// agents.
/// </summary>
public sealed class ProjectionRebuildGateTests
{
    private static ClusterNodeInfo Node(bool self) =>
        new(Guid.NewGuid(), 1, "n", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsLeader: self, IsSelf: self);

    [Fact]
    public void An_unknown_topology_refuses_the_rebuild()
    {
        var refused = ProjectionEndpoints.RefuseUnlessSingleInstance(null);
        Assert.NotNull(refused);
        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)refused!).StatusCode);
    }

    [Fact]
    public void Several_live_nodes_refuse_and_a_single_node_allows()
    {
        Assert.NotNull(ProjectionEndpoints.RefuseUnlessSingleInstance([Node(true), Node(false)]));
        Assert.Null(ProjectionEndpoints.RefuseUnlessSingleInstance([Node(true)]));
    }
}
