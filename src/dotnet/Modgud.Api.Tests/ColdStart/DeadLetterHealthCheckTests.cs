using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modgud.Api.HealthChecks;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Plan „Teilfehler nach dem Commit“ — a follow-up that failed for good must not go unnoticed:
/// the readiness check "dead-letters" turns Degraded and names the realm.
/// </summary>
public class DeadLetterHealthCheckTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    private const string Realm = "dl-acme";

    [Fact]
    public async Task A_follow_up_that_failed_for_good_makes_the_dead_letter_check_degraded()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var created = await factory.Services.GetRequiredService<IRealmProvisioningService>().CreateRealmAsync(new CreateRealmDto
        {
            Slug = Realm,
            DisplayName = "Dead-letter Acme",
            Domains = [$"{Realm}.localhost"],
            InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@dl-acme.test" },
        }, ct);
        Assert.False(created.IsError);

        var monitor = factory.Services.GetRequiredService<DeadLetterMonitor>();
        await monitor.SampleAsync(ct);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(factory, ct)).Status);

        var id = Guid.NewGuid();
        using (TenantContext.Enter(Realm))
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await scope.ServiceProvider.GetRequiredService<IFollowUpOutbox>()
                .PublishAsync(session, new FollowUpProbe(id, Permanent: true));
            await session.SaveChangesAsync(ct);
        }

        // A permanent failure skips the retry schedule: one attempt, then a dead letter.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        DeadLetterSnapshot snapshot;
        while ((snapshot = await monitor.SampleAsync(ct)).Total == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No dead letter appeared.");
            await Task.Delay(200, ct);
        }

        Assert.Equal(1, snapshot.PerRealm[Realm]);
        Assert.Equal(1, factory.Services.GetRequiredService<FollowUpProbeRecorder>().Attempts(id));
        var report = await CheckAsync(factory, ct);
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains(Realm, report.Description);
    }

    private static async Task<HealthReportEntry> CheckAsync(ColdStartWebApplicationFactory factory, CancellationToken ct)
    {
        var report = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "dead-letters", ct);
        return report.Entries["dead-letters"];
    }
}
