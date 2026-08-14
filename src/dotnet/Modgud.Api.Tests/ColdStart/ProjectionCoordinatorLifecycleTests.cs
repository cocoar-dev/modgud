using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.Marten;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Pins the deterministic projection topology for the behavioural test suite.
/// These hosts must never run Marten's background coordinator beside explicit
/// database resets and interactive catch-up barriers.
/// </summary>
public sealed class ProjectionCoordinatorLifecycleTests(ColdStartFixture fixture)
    : ColdStartTestBase(fixture)
{
    [Fact]
    public void Behavioural_test_host_has_no_background_projection_coordinator()
    {
        var coordinatorControl = Factory.Services.GetRequiredService<IProjectionCoordinatorControl>();

        Assert.False(coordinatorControl.IsBackgroundDaemonEnabled);
        Assert.Null(Factory.Services.GetService<IProjectionCoordinator>());
    }

    [Fact]
    public async Task Production_shaped_host_runs_maintenance_and_shuts_down_cleanly()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync(
            useBackgroundProjectionDaemon: true);
        var coordinatorControl = host.Services.GetRequiredService<IProjectionCoordinatorControl>();

        Assert.True(coordinatorControl.IsBackgroundDaemonEnabled);
        Assert.NotNull(host.Services.GetService<IProjectionCoordinator>());
        Assert.False(host.Services.GetRequiredService<IOptions<HostOptions>>()
            .Value.ServicesStopConcurrently);

        var user = await host.Factory.CreateTestUserAsync(
            firstname: "Solo",
            lastname: "Lifecycle",
            acronym: "sl",
            email: "solo-lifecycle@test.local");

        Assert.Equal("Solo", user.Firstname);
        Assert.Equal("Lifecycle", user.Lastname);
    }
}
