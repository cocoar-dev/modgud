using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B8-AJ-01 — the untrusted pool's limit holds for overlapping requests too. The check
/// used to only peek at the bucket and the failure was charged after the password check,
/// so N simultaneous wrong passwords were all checked against a limit of five.
/// AccessFailedCount rises only for attempts that passed the throttle AND had a wrong
/// password verified, so it counts exactly the password checks the throttle admitted.
/// </summary>
public partial class LoginThrottleTests
{
    [Fact]
    public async Task Overlapping_wrong_passwords_are_checked_at_most_up_to_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchLoginAsync(target: Rule(5, 15), mode: RateLimitEnforcementMode.Enforce);
        var (userName, _) = await CreateUserAsync("parallel");

        var attempts = Enumerable.Range(0, 30)
            .Select(i => LoginAsync(Factory.CreateClient(), userName, "wrong-" + i))
            .ToArray();
        var responses = await Task.WhenAll(attempts);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(userName);
        var checkedPasswords = await users.GetAccessFailedCountAsync(user!);
        Assert.True(checkedPasswords <= 5, $"{checkedPasswords} wrong passwords were checked against a limit of 5");
    }

    [Fact]
    public async Task Successful_logins_do_not_use_up_the_untrusted_pool()
    {
        await PatchLoginAsync(target: Rule(3, 15), mode: RateLimitEnforcementMode.Enforce);
        var (userName, _) = await CreateUserAsync("successes");

        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Factory.CreateClient(), userName, Password)).StatusCode);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Factory.CreateClient(), userName, "wrong-" + i)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Factory.CreateClient(), userName, Password)).StatusCode);
    }
}
