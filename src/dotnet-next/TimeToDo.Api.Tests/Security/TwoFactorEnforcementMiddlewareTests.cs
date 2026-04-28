using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api;
using TimeToDo.Authentication;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Domain;

namespace TimeToDo.Api.Tests.Security;

/// <summary>
/// Verifies that server-side 2FA enforcement blocks API access when the grace period has
/// expired, even if the client bypasses the SPA's SecureSetupModal. The login endpoint
/// sets a valid auth cookie before the check, so without this middleware a curl or an old
/// tab could reach every API endpoint with just a password.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TwoFactorEnforcementMiddlewareTests : IntegrationTestBase
{
    public TwoFactorEnforcementMiddlewareTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AtLevel0_NoEnforcement_RegardlessOfDueAt()
    {
        // Default fixture level is 0 — middleware must not block even with expired grace.
        await SetSecureSetupDueAtAsync(DefaultUser!.Id, DateTime.UtcNow.AddDays(-5));
        var response = await Client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AtLevel1_NoTwoFactor_ExpiredGrace_Blocks403()
    {
        using var _ = TemporarilySetLevel(1);
        var (client, userId) = await CreateUserAndLoginAsync("block1", "Block", "One");
        await SetSecureSetupDueAtAsync(userId, DateTime.UtcNow.AddDays(-1));

        var response = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("RequiresSecureSetup").GetBoolean());
        Assert.False(body.GetProperty("GracePeriod").GetBoolean());
    }

    [Fact]
    public async Task AtLevel1_NoTwoFactor_WithinGrace_Allows()
    {
        using var _ = TemporarilySetLevel(1);
        var (client, userId) = await CreateUserAndLoginAsync("allow1", "Allow", "One");
        await SetSecureSetupDueAtAsync(userId, DateTime.UtcNow.AddDays(5));

        var response = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AtLevel1_WithTwoFactor_Allows_EvenWithExpiredDueAt()
    {
        using var _ = TemporarilySetLevel(1);
        // Default user gets email OTP enabled → counts as 2FA
        var enableResponse = await Client.PostAsJsonAsync("/api/account/email-otp/enable", new { }, TestContext.Current.CancellationToken);
        Assert.True(enableResponse.IsSuccessStatusCode);

        // Stamp an expired DueAt — should not matter because user has 2FA
        await SetSecureSetupDueAtAsync(DefaultUser!.Id, DateTime.UtcNow.AddDays(-10));

        var response = await Client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AtLevel1_SetupEndpoints_Whitelisted_EvenWhenBlocked()
    {
        using var _ = TemporarilySetLevel(1);
        var (client, userId) = await CreateUserAndLoginAsync("setup1", "Setup", "One");
        await SetSecureSetupDueAtAsync(userId, DateTime.UtcNow.AddDays(-1));

        // /api/account/me must pass so the SPA knows state
        var meResponse = await client.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        // /api/account/mfa/setup must pass so user can enroll
        var setupResponse = await client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        // /api/account/logout must pass so user can escape
        var logoutResponse = await client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);
    }

    [Fact]
    public async Task AtLevel1_NullDueAt_LazyStampsAndAllows()
    {
        using var _ = TemporarilySetLevel(1);
        var (client, userId) = await CreateUserAndLoginAsync("lazy1", "Lazy", "Stamp");
        // Simulate a cookie issued before enforcement: clear DueAt after login stamped it
        await SetSecureSetupDueAtAsync(userId, null);

        // Next request should lazy-stamp (not block) and let through
        var response = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // DueAt is now populated
        await using var query = Factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var securityData = await query.LoadAsync<UserSecurityData>(userId, TestContext.Current.CancellationToken);
        Assert.NotNull(securityData?.SecureSetupDueAt);
        Assert.InRange(securityData!.SecureSetupDueAt!.Value - DateTime.UtcNow,
            TimeSpan.FromDays(13), TimeSpan.FromDays(15));
    }

    [Fact]
    public async Task AnonymousEndpoint_StaysReachable_EvenForExpiredUser()
    {
        // Regression: without respecting [AllowAnonymous], the /login page would infinite-loop.
        // It calls /api/app-info which is anonymous but was being blocked for authenticated
        // users past grace — SPA received 403 → redirect to /login → /api/app-info → 403 → …
        using var _ = TemporarilySetLevel(1);
        var (client, userId) = await CreateUserAndLoginAsync("anon1", "Anon", "Test");
        await SetSecureSetupDueAtAsync(userId, DateTime.UtcNow.AddDays(-1));

        var appInfo = await client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, appInfo.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedRequest_IsNotAffected()
    {
        using var _ = TemporarilySetLevel(1);
        var client = Factory.CreateClient();

        // Anonymous endpoint stays anonymous
        var appInfo = await client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, appInfo.StatusCode);

        // Protected endpoint returns its normal 401 (not 403 from enforcement)
        var todos = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, todos.StatusCode);
    }

    // ── Helpers ──

    private async Task<(HttpClient client, Guid userId)> CreateUserAndLoginAsync(
        string userName, string firstname, string lastname)
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: firstname, lastname: lastname, acronym: userName.ToUpper(),
            email: $"{userName}@test.com", password: "TestPass1234", permissions: ["app:admin"]);
        var client = await CreateAuthenticatedClientAsync(userName, "TestPass1234");
        return (client, user.Id);
    }

    private async Task SetSecureSetupDueAtAsync(Guid userId, DateTime? dueAt)
    {
        await using var session = Factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var securityData = await session.LoadAsync<UserSecurityData>(userId, TestContext.Current.CancellationToken)
            ?? UserSecurityData.Create(userId);
        securityData.SecureSetupDueAt = dueAt;
        session.Store(securityData);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

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
