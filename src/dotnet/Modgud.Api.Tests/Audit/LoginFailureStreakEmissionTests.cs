using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;

namespace Modgud.Api.Tests.Audit;

/// <summary>
/// Proves the Phase 1 emission logic in <c>EventSourcedUserStore.ResetAccessFailedCountAsync</c>
/// (it moved there from the former <c>AppendSecurityChangeEvents</c> diff when the
/// lockout counter was made concurrency-safe — see P0-4 / LockoutConcurrencyTests):
/// a known-user failure streak is recorded as exactly ONE aggregated
/// <see cref="UserLoginFailuresObservedEvent"/> when the access-failed counter
/// resolves (>0 → 0), not one event per attempt (Decision (b)).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class LoginFailureStreakEmissionTests : IntegrationTestBase
{
    public LoginFailureStreakEmissionTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Resolving_a_failure_streak_emits_one_aggregated_event_with_the_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Streak", "Resolver", "sr", "streak@acme.com");

        using (var scope = Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var u = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(u);

            // Three failed attempts (below the lockout threshold): the counter goes
            // 0 → 3. No event yet — failures are NOT recorded per attempt.
            await userManager.AccessFailedAsync(u!);
            await userManager.AccessFailedAsync(u!);
            await userManager.AccessFailedAsync(u!);

            // The streak resolves (what a successful sign-in does): 3 → 0 → ONE event.
            await userManager.ResetAccessFailedCountAsync(u!);
        }

        await using var qs = GetTenantedSession();
        var stream = await qs.Events.FetchStreamAsync(user.Id, token: ct);
        var observed = stream
            .Select(e => e.Data)
            .OfType<UserLoginFailuresObservedEvent>()
            .ToList();

        var ev = Assert.Single(observed);
        Assert.Equal(3, ev.FailedCount);
        Assert.Equal(user.Id, ev.UserId);
    }
}
