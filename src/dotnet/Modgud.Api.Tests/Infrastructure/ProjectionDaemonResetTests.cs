using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure.Persistence.Marten;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Pins the lifecycle contract between the shared integration-test host and
/// Marten projection execution. Resetting event data restarts sequence numbers,
/// so the deterministic behavioural suite must not retain a background daemon
/// carrying the previous database's in-memory high-water state.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProjectionDaemonResetTests : IntegrationTestBase
{
    public ProjectionDaemonResetTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Every_reset_projects_the_new_sequence_without_a_background_daemon()
    {
        var coordinatorControl = Factory.Services.GetRequiredService<IProjectionCoordinatorControl>();
        Assert.False(coordinatorControl.IsBackgroundDaemonEnabled);

        for (var cycle = 1; cycle <= 10; cycle++)
        {
            await Factory.ResetMartenDataAsync();

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
