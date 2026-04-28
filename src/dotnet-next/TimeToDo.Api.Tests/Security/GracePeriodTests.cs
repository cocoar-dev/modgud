using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api;
using TimeToDo.Authentication;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Domain;

namespace TimeToDo.Api.Tests.Security;

/// <summary>
/// 2FA grace period tests. At AuthenticationMinimumLevel >= 1, users without any 2FA
/// method get TwoFactorGracePeriodDays after their first post-enforcement login to set
/// one up. During grace the login succeeds with GracePeriod=true; after expiry the
/// modal becomes blocking (GracePeriod=false).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class GracePeriodTests : IntegrationTestBase
{
    public GracePeriodTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ── app-info exposes the configured grace period ──

    [Fact]
    public async Task AppInfo_IncludesTwoFactorGracePeriodDays()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("TwoFactorGracePeriodDays", out var days));
        Assert.Equal(14, days.GetInt32()); // default
    }

    // ── /me exposes the user's grace due date ──

    [Fact]
    public async Task Me_SecureSetupDueAt_IsAbsentOrNull_ForFreshUser()
    {
        // Global JSON option ignores nulls, so the field may be omitted entirely when unset.
        // Either way, it must not carry a value for a fresh user who hasn't hit Level-1 yet.
        var response = await Client.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        if (body.TryGetProperty("SecureSetupDueAt", out var due))
            Assert.Equal(JsonValueKind.Null, due.ValueKind);
    }

    [Fact]
    public async Task Me_SecureSetupDueAt_IsReturned_WhenSet()
    {
        var dueAt = DateTime.UtcNow.AddDays(7);
        await SetSecureSetupDueAtAsync(DefaultUser!.Id, dueAt);

        var response = await Client.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("SecureSetupDueAt", out var due));
        Assert.Equal(JsonValueKind.String, due.ValueKind);
        Assert.InRange(due.GetDateTime(), dueAt.AddSeconds(-1), dueAt.AddSeconds(1));
    }

    // ── Login at Level 1 starts the grace clock on first trigger ──

    [Fact]
    public async Task Login_AtLevel1_WithNo2FA_StartsGrace_OnFirstTrigger()
    {
        using var level = TemporarilySetLevel(1);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Grace", lastname: "User", acronym: "GR",
            email: "grace@test.com", password: "TestPass1234", permissions: []);

        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "gr", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("RequiresSecureSetup").GetBoolean());
        Assert.True(body.GetProperty("GracePeriod").GetBoolean());
        Assert.True(body.TryGetProperty("SecureSetupDueAt", out var due));
        Assert.Equal(JsonValueKind.String, due.ValueKind);

        // Verify DB state: DueAt is ~14 days in the future
        await using var session = Factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(securityData);
        Assert.NotNull(securityData.SecureSetupDueAt);
        var expectedDueIn = TimeSpan.FromDays(14);
        var actualDueIn = securityData.SecureSetupDueAt.Value - DateTime.UtcNow;
        Assert.InRange(actualDueIn, expectedDueIn - TimeSpan.FromMinutes(1), expectedDueIn + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Login_AtLevel1_WithExistingGrace_DoesNotResetClock()
    {
        using var level = TemporarilySetLevel(1);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Sticky", lastname: "Grace", acronym: "SG",
            email: "sg@test.com", password: "TestPass1234", permissions: []);

        // Pre-stamp a due date in the past-ish — simulating "grace started 10 days ago"
        var stampedAt = DateTime.UtcNow.AddDays(4); // 4 days remaining
        await SetSecureSetupDueAtAsync(user.Id, stampedAt);

        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "sg", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("GracePeriod").GetBoolean());
        var returnedDue = body.GetProperty("SecureSetupDueAt").GetDateTime();
        // Due date should be the originally-stamped one, not refreshed
        Assert.InRange(returnedDue, stampedAt.AddSeconds(-1), stampedAt.AddSeconds(1));
    }

    [Fact]
    public async Task Login_AtLevel1_WithExpiredGrace_ReturnsBlockingSetup()
    {
        using var level = TemporarilySetLevel(1);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Expired", lastname: "User", acronym: "EX",
            email: "ex@test.com", password: "TestPass1234", permissions: []);

        await SetSecureSetupDueAtAsync(user.Id, DateTime.UtcNow.AddDays(-1));

        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "ex", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("RequiresSecureSetup").GetBoolean());
        Assert.False(body.GetProperty("GracePeriod").GetBoolean());
    }

    // ── Admin Reset ──

    [Fact]
    public async Task AdminResetGrace_ExtendsDueAt_ByConfiguredDays()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Reset", lastname: "Target", acronym: "RT",
            email: "rt@test.com", password: "TestPass1234", permissions: []);

        var shortId = new ShortGuid(user.Id).ToString();
        var response = await Client.PostAsync($"/api/admin/users/{shortId}/grace/reset", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var due = body.GetProperty("SecureSetupDueAt").GetDateTime();
        var expectedDueIn = TimeSpan.FromDays(14);
        var actualDueIn = due - DateTime.UtcNow;
        Assert.InRange(actualDueIn, expectedDueIn - TimeSpan.FromMinutes(1), expectedDueIn + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AdminResetGrace_UsesPerUserOverride_NotGlobalDefault()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Override", lastname: "Reset", acronym: "OR",
            email: "or@test.com", password: "TestPass1234", permissions: []);
        var shortId = new ShortGuid(user.Id).ToString();

        // Set per-user override of 60 days
        await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = 60, TwoFactorExempt = (bool?)null }, TestContext.Current.CancellationToken);

        // Reset should extend by 60, not the global 14
        var response = await Client.PostAsync($"/api/admin/users/{shortId}/grace/reset", null, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var due = body.GetProperty("SecureSetupDueAt").GetDateTime();
        var delta = due - DateTime.UtcNow;
        Assert.InRange(delta, TimeSpan.FromDays(59), TimeSpan.FromDays(61));
    }

    [Fact]
    public async Task AdminClearGrace_ExpiresDueAtImmediately()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Clear", lastname: "Target", acronym: "CT",
            email: "ct@test.com", password: "TestPass1234", permissions: []);

        await SetSecureSetupDueAtAsync(user.Id, DateTime.UtcNow.AddDays(5));

        var shortId = new ShortGuid(user.Id).ToString();
        var response = await Client.DeleteAsync($"/api/admin/users/{shortId}/grace", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // DueAt is set to "now" (not null) so the middleware's DueAt > now check fails
        // and it blocks. If this were null, middleware would lazy-stamp a fresh 14d grace.
        await using var session = Factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(securityData);
        Assert.NotNull(securityData.SecureSetupDueAt);
        Assert.True(securityData.SecureSetupDueAt.Value <= DateTime.UtcNow);
    }

    // ── Per-user policy (override + exempt) ──

    [Fact]
    public async Task AdminSetPolicy_WritesOverride_AndIsReadBack()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Policy", lastname: "Target", acronym: "PT",
            email: "pt@test.com", password: "TestPass1234", permissions: []);

        var shortId = new ShortGuid(user.Id).ToString();
        var put = await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = 365, TwoFactorExempt = false }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var info = await (await Client.GetAsync($"/api/admin/users/{shortId}/security-info", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(365, info.GetProperty("GracePeriodDaysOverride").GetInt32());
        Assert.False(info.GetProperty("TwoFactorExempt").GetBoolean());
    }

    [Fact]
    public async Task AdminSetPolicy_WithMinusOne_ClearsOverride()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Clear", lastname: "Override", acronym: "CO",
            email: "co-policy@test.com", password: "TestPass1234", permissions: []);

        var shortId = new ShortGuid(user.Id).ToString();
        // First set to 30 days
        await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = 30, TwoFactorExempt = (bool?)null }, TestContext.Current.CancellationToken);
        // Then clear with -1
        var cleared = await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = -1, TwoFactorExempt = (bool?)null }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var info = await (await Client.GetAsync($"/api/admin/users/{shortId}/security-info", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        // Null fields are omitted by JSON options — override should be absent
        Assert.False(info.TryGetProperty("GracePeriodDaysOverride", out var prop) && prop.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Login_UsesPerUserOverride_NotGlobalDefault()
    {
        // Pre-condition check separated from the Level-1 run so the fixture can mutate state
        // without fighting parallel tests (the collection is non-parallel).
        using var level = TemporarilySetLevel(1);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Override", lastname: "Grace", acronym: "OG",
            email: "og@test.com", password: "TestPass1234", permissions: []);

        // Admin sets the override to 90 days
        var shortId = new ShortGuid(user.Id).ToString();
        await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = 90, TwoFactorExempt = (bool?)null }, TestContext.Current.CancellationToken);

        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "og", Password = "TestPass1234" }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // DueAt should be ~90 days in the future, not 14
        var due = body.GetProperty("SecureSetupDueAt").GetDateTime();
        var delta = due - DateTime.UtcNow;
        Assert.InRange(delta, TimeSpan.FromDays(89), TimeSpan.FromDays(91));
    }

    [Fact]
    public async Task Login_WhenExempt_SkipsSecureSetupEntirely()
    {
        using var level = TemporarilySetLevel(1);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Service", lastname: "Account", acronym: "SA",
            email: "sa@test.com", password: "TestPass1234", permissions: []);

        var shortId = new ShortGuid(user.Id).ToString();
        await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = (int?)null, TwoFactorExempt = true }, TestContext.Current.CancellationToken);

        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "sa", Password = "TestPass1234" }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // Login is "successful" directly — no RequiresSecureSetup flag
        Assert.False(body.TryGetProperty("RequiresSecureSetup", out _));
        Assert.Equal("Login successful", body.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Middleware_WhenExempt_AllowsApiDespiteExpiredGrace()
    {
        using var level = TemporarilySetLevel(1);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Exempt", lastname: "User", acronym: "EU",
            email: "eu@test.com", password: "TestPass1234", permissions: ["app:admin"]);
        var client = await CreateAuthenticatedClientAsync("eu", "TestPass1234");

        // Expire grace THEN exempt the user — middleware should let through on flag
        await SetSecureSetupDueAtAsync(user.Id, DateTime.UtcNow.AddDays(-5));
        var shortId = new ShortGuid(user.Id).ToString();
        await Client.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy",
            new { GracePeriodDaysOverride = (int?)null, TwoFactorExempt = true }, TestContext.Current.CancellationToken);

        var todos = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, todos.StatusCode);
    }

    [Fact]
    public async Task AdminGraceEndpoints_RequireAppAdmin()
    {
        var nonAdmin = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Non", lastname: "Admin", acronym: "NA",
            email: "na-grace@test.com", password: "TestPass1234", permissions: []);

        var nonAdminClient = await CreateAuthenticatedClientAsync("na", "TestPass1234");
        var shortId = new ShortGuid(nonAdmin.Id).ToString();

        var resetResponse = await nonAdminClient.PostAsync($"/api/admin/users/{shortId}/grace/reset", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resetResponse.StatusCode);

        var clearResponse = await nonAdminClient.DeleteAsync($"/api/admin/users/{shortId}/grace", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, clearResponse.StatusCode);

        var policyResponse = await nonAdminClient.PutAsJsonAsync($"/api/admin/users/{shortId}/grace/policy", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, policyResponse.StatusCode);
    }

    // ── Last-Method Disable expires the grace immediately (no 409 anymore) ──

    [Fact]
    public async Task EmailOtpDisable_LastMethod_AtLevel1_Succeeds_AndExpiresGrace()
    {
        using var level = TemporarilySetLevel(1);

        // Default user has password but no 2FA. Enable Email-OTP only, then disable it.
        var enable = await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        Assert.True(enable.IsSuccessStatusCode);

        var disable = await Client.PostAsync("/api/account/email-otp/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var body = await disable.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("SecureSetupRequired").GetBoolean());

        await using var session = Factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var security = await session.LoadAsync<UserSecurityData>(DefaultUser!.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(security?.SecureSetupDueAt);
        Assert.True(security!.SecureSetupDueAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task MfaDisable_AtLevel0_DoesNotSetSecureSetupRequired()
    {
        // At Level 0 enforcement isn't active — the grace flag stays untouched even on
        // last-method disable, so the response carries SecureSetupRequired=false.
        await SetupTotpForDefaultUserAsync();

        var response = await Client.PostAsync("/api/account/mfa/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("SecureSetupRequired").GetBoolean());
    }

    [Fact]
    public async Task EmailOtpDisable_NotLastMethod_AtLevel1_DoesNotExpireGrace()
    {
        using var level = TemporarilySetLevel(1);

        // Activate two methods; disable Email-OTP — TOTP still remains, so grace untouched.
        await SetupTotpForDefaultUserAsync();
        var enable = await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        Assert.True(enable.IsSuccessStatusCode);

        // Pre-stamp a future grace just to verify it doesn't get clobbered.
        var futureDue = DateTime.UtcNow.AddDays(30);
        await SetSecureSetupDueAtAsync(DefaultUser!.Id, futureDue);

        var disable = await Client.PostAsync("/api/account/email-otp/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        var body = await disable.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("SecureSetupRequired").GetBoolean());

        await using var session = Factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var security = await session.LoadAsync<UserSecurityData>(DefaultUser.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(security?.SecureSetupDueAt);
        // Future stamp is preserved (within a few seconds)
        Assert.True((security!.SecureSetupDueAt!.Value - futureDue).Duration() < TimeSpan.FromSeconds(5));
    }

    private async Task SetupTotpForDefaultUserAsync()
    {
        var setup = await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        var body = await setup.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var sharedKey = body.GetProperty("SharedKey").GetString()!;
        var code = AuthEnforcementTests.GenerateTotpForTest(sharedKey);
        var verify = await Client.PostAsJsonAsync("/api/account/mfa/verify", new { Code = code }, TestContext.Current.CancellationToken);
        Assert.True(verify.IsSuccessStatusCode);
    }

    // ── Helpers ──

    private async Task SetSecureSetupDueAtAsync(Guid userId, DateTime dueAt)
    {
        await using var session = Factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var securityData = await session.LoadAsync<UserSecurityData>(userId, TestContext.Current.CancellationToken)
            ?? UserSecurityData.Create(userId);
        securityData.SecureSetupDueAt = dueAt;
        session.Store(securityData);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The fixture's AppSettings singleton is mutated for the scope of this using block.
    /// The level is reset to the default (0) on Dispose so sibling tests aren't affected.
    /// </summary>
    private IDisposable TemporarilySetLevel(int level)
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        var original = settings.AuthenticationMinimumLevel;
        settings.AuthenticationMinimumLevel = level;
        return new LevelResetter(settings, original);
    }

    private sealed class LevelResetter(AppSettings settings, int original) : IDisposable
    {
        public void Dispose() => settings.AuthenticationMinimumLevel = original;
    }
}
