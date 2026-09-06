using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Devices;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR 0020 — device-aware login throttling end-to-end: the device cookie on success,
/// the untrusted pool refusing strangers while the owner's device keeps working, the
/// unlock e-mail once per window, log-only, and the signal-only source cell.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class LoginThrottleTests : IntegrationTestBase
{
    public LoginThrottleTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Password = "TestPass1234";

    [Fact]
    public async Task A_successful_login_issues_the_device_cookie()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userName, _) = await CreateUserAsync("dev-cookie");
        var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/account/login", new { UserName = userName, Password }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        var device = Assert.Single(cookies, c => c.StartsWith(TrustedDevice.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", device, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", device, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Strangers_exhaust_the_untrusted_pool_but_the_owners_device_keeps_working()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchLoginAsync(target: Rule(3, 15), mode: RateLimitEnforcementMode.Enforce);
        var (userName, _) = await CreateUserAsync("owner");

        // The owner logged in from this browser before: it holds a trusted device cookie.
        var owner = await CreateAuthenticatedClientAsync(userName, Password);

        // Three wrong passwords from a browser without a cookie fill the untrusted pool.
        var stranger = Factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var failed = await LoginAsync(stranger, userName, "wrong-" + i);
            Assert.True(failed.StatusCode == HttpStatusCode.Unauthorized,
                $"attempt {i}: {failed.StatusCode} — {await failed.Content.ReadAsStringAsync(ct)}");
        }

        // Even the CORRECT password is refused from an untrusted client now — with the
        // very same body a wrong password gets.
        var refused = await LoginAsync(Factory.CreateClient(), userName, Password);
        var refusedBody = await refused.Content.ReadAsStringAsync(ct);
        Assert.True(refused.StatusCode == HttpStatusCode.Unauthorized, $"{refused.StatusCode} — {refusedBody}");
        Assert.Contains("Invalid credentials", refusedBody);

        // The owner's own device is in its own bucket: untouched.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(owner, userName, Password)).StatusCode);
    }

    [Fact]
    public async Task The_owner_gets_one_unlock_mail_per_window()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchLoginAsync(target: Rule(2, 15), mode: RateLimitEnforcementMode.Enforce);
        var (userName, email) = await CreateUserAsync("unlock");
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>();
        var before = mail.GetSentEmails().Count(m => m.To == email);

        var stranger = Factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await LoginAsync(stranger, userName, "wrong-" + i);

        var unlock = mail.GetSentEmails().Where(m => m.To == email).Skip(before).ToList();
        var single = Assert.Single(unlock);
        Assert.Contains("magic-login?userId=", single.HtmlBody);
        Assert.Contains("blockiert", single.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Log_only_mode_lets_the_exhausted_pool_through()
    {
        await PatchLoginAsync(target: Rule(1, 15), mode: RateLimitEnforcementMode.LogOnly);
        var (userName, _) = await CreateUserAsync("logonly");

        var stranger = Factory.CreateClient();
        await LoginAsync(stranger, userName, "wrong");
        await LoginAsync(stranger, userName, "wrong");

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Factory.CreateClient(), userName, Password)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_untrusted_bucket_never_refuses()
    {
        await PatchLoginAsync(target: Rule(1, 15) with { Enabled = false }, mode: RateLimitEnforcementMode.Enforce);
        var (userName, _) = await CreateUserAsync("disabled");

        var stranger = Factory.CreateClient();
        for (var i = 0; i < 4; i++) await LoginAsync(stranger, userName, "wrong");

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Factory.CreateClient(), userName, Password)).StatusCode);
    }

    [Fact]
    public async Task The_login_source_cell_is_a_signal_and_cannot_be_switched_off()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = CreateTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();

        var off = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            AuthRateLimits = new UpdateAuthRateLimitsDto
            {
                Policies = new() { ["login"] = new UpdatePolicyLimitsDto { Source = new Optional<RateLimitRuleDto?>(Rule(50, 15) with { Enabled = false }) } },
            },
        }, ct);
        Assert.True(off.IsError);
        Assert.Equal("AuthRateLimits.login.Source", off.FirstError.Code);

        var tuned = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            AuthRateLimits = new UpdateAuthRateLimitsDto
            {
                Policies = new()
                {
                    ["login"] = new UpdatePolicyLimitsDto
                    {
                        Source = new Optional<RateLimitRuleDto?>(Rule(50, 15)),
                        Device = new Optional<RateLimitRuleDto?>(Rule(20, 30)),
                    },
                },
            },
        }, ct);
        Assert.False(tuned.IsError, tuned.IsError ? tuned.FirstError.Description : "");

        var read = RealmSettingsService.MapAuthRateLimitsToDto((await settings.LoadAsync(ct)).AuthRateLimits);
        var login = read.Policies["login"];
        Assert.True(login.Source!.SignalOnly);
        Assert.Equal(50, login.Source.PermitLimit);
        Assert.Equal(20, login.Device!.PermitLimit);
        Assert.Null(login.Client);
        Assert.False(read.Defaults["native-otp"].Source!.SignalOnly);

        // Back to defaults for the other tests.
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            AuthRateLimits = new UpdateAuthRateLimitsDto { Policies = new() { ["login"] = null } },
        }, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RateLimitRuleDto Rule(int limit, int window) => new() { PermitLimit = limit, WindowMinutes = window };

    private async Task PatchLoginAsync(RateLimitRuleDto target, RateLimitEnforcementMode mode)
    {
        using var scope = CreateTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var result = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            AuthRateLimits = new UpdateAuthRateLimitsDto
            {
                Policies = new() { ["login"] = new UpdatePolicyLimitsDto { Target = new Optional<RateLimitRuleDto?>(target) } },
                Mode = new Optional<RateLimitEnforcementMode?>(mode),
                ClearLegacy = true,
            },
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
    }

    /// <summary>Test users are created with a confirmed address — the unlock mail
    /// requires one.</summary>
    private async Task<(string UserName, string Email)> CreateUserAsync(string tag)
    {
        var acronym = $"lt{tag}{Guid.NewGuid():N}"[..16];
        var email = $"{acronym}@login-throttle.example";
        await Factory.CreateTestUserWithIdentityAsync("Login", tag, acronym, email, Password);
        return (acronym, email);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/account/login", new { UserName = userName, Password = password }, TestContext.Current.CancellationToken);

    private IServiceScope CreateTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
