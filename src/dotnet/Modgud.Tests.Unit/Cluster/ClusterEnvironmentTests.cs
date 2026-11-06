using Modgud.Infrastructure.Cluster;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>
/// B9-CL01 (A) — the node table is what tells the projection rebuild gate, the CLI rebuild
/// check and readiness that another instance runs. Staging is where several instances are
/// tried before Production, so it must run the cluster-capable path too.
/// </summary>
public class ClusterEnvironmentTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("staging", true)]
    [InlineData("Development", false)]
    [InlineData("Testing", false)]
    public void Production_and_staging_run_the_cluster_capable_path(string environment, bool expected)
    {
        Assert.Equal(expected, ClusterHostingOptions.IsClusterCapableEnvironment(environment));
    }
}
