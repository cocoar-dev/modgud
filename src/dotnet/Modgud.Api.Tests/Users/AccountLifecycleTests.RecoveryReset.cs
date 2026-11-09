using System.Net;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// RV-0004 — <c>recover reset-2fa</c> removed every second factor but revoked no existing
/// access: an open browser session (and the tokens and staffing sessions of the account)
/// stayed valid, unlike the self-service paths (TOTP disable revokes tokens, passkey delete
/// ends staffing). A factor reset is a recovery action; the account signs in afresh.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task Recovery_reset_2fa_ends_the_accounts_existing_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var userName = $"rst{Guid.NewGuid():N}"[..12];
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Reset", lastname: "Factors", acronym: userName, email: $"{userName}@test.com",
            password: DefaultPassword, isRealmAdmin: false);

        using var browser = await CreateAuthenticatedClientAsync(userName, DefaultPassword);
        using (var before = await browser.GetAsync("/api/account/me", ct))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var result = await CliHarness.RunAsync(Factory.Services, "reset-2fa", userName, "--realm", "system");
        Assert.True(result.ExitCode == 0, $"reset-2fa failed: {result.StdErr}");

        using var after = await browser.GetAsync("/api/account/me", ct);
        Assert.False(after.IsSuccessStatusCode,
            $"the session opened before the reset must end, got {(int)after.StatusCode}");
    }
}
