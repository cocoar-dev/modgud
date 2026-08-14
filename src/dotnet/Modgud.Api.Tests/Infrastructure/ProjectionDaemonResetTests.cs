using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Pins the lifecycle contract between the shared integration-test host and
/// Marten's async projection daemon. Resetting event data restarts sequence
/// numbers, so no daemon carrying the previous database's in-memory high-water
/// state may survive the reset.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProjectionDaemonResetTests : IntegrationTestBase
{
    public ProjectionDaemonResetTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Every_reset_discards_cached_daemon_before_projecting_new_sequence()
    {
        var coordinator = Factory.Services.GetRequiredService<IProjectionCoordinator>();

        for (var cycle = 1; cycle <= 10; cycle++)
        {
            var daemonBeforeReset = await coordinator.DaemonForDatabase("system");

            await Factory.ResetMartenDataAsync();

            var daemonAfterReset = await coordinator.DaemonForDatabase("system");
            Assert.NotSame(daemonBeforeReset, daemonAfterReset);

            var user = await Factory.CreateTestUserAsync(
                firstname: "After",
                lastname: $"Reset{cycle}",
                acronym: $"a{cycle}",
                email: $"after-reset-{cycle}@test.com");

            Assert.Equal("After", user.Firstname);
            Assert.Equal($"Reset{cycle}", user.Lastname);
        }
    }
}
