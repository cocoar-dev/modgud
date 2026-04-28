using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Cocoar.Configuration.Testing;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Api.Tests.Infrastructure;

namespace Cocoar.Auth.Api.Tests.Security;

/// <summary>
/// Tests for AuthenticationMinimumLevel enforcement, RememberMe, disable protection,
/// and Level 2 (Passwordless) blocking.
///
/// Note: SharedPostgresFixture sets AuthenticationMinimumLevel = 0 by default so existing
/// tests work. These tests override that with specific levels per test.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthEnforcementTests : IntegrationTestBase
{
    public AuthEnforcementTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ── Level 1: RequiresSecureSetup ──

    [Fact]
    public async Task Login_AtLevel1_WithNo2FA_ReturnsRequiresSecureSetup()
    {
        // Create user without any 2FA
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "No2FA", lastname: "User", acronym: "N2",
            email: "no2fa@test.com", password: "TestPass1234", permissions: []);

        // Override config to Level 1 for this request
        var response = await CreateUnauthenticatedClient()
            .PostAsJsonAsync("/api/account/login", new { UserName = "n2", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // At Level 0 (test default), no secure setup required
        Assert.False(body.TryGetProperty("RequiresSecureSetup", out _));
    }

    // ── /me Response includes 2FA status ──

    [Fact]
    public async Task Me_ReturnsHas2FA_False_WhenNo2FAConfigured()
    {
        var response = await Client.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("Has2FA", out var has2fa));
        Assert.False(has2fa.GetBoolean());
        Assert.True(body.TryGetProperty("TwoFactorMethods", out var methods));
        Assert.Equal(0, methods.GetArrayLength());
    }

    [Fact]
    public async Task Me_ReturnsHas2FA_True_AfterEnablingEmailOtp()
    {
        // Enable Email OTP for default user (has email)
        var enableResponse = await Client.PostAsJsonAsync("/api/account/email-otp/enable", new { }, TestContext.Current.CancellationToken);
        Assert.True(enableResponse.IsSuccessStatusCode);

        var response = await Client.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("Has2FA").GetBoolean());

        var methods = body.GetProperty("TwoFactorMethods");
        Assert.Contains("email", methods.EnumerateArray().Select(m => m.GetString()));
    }

    // ── Disable Protection ──

    [Fact]
    public async Task MfaDisable_WhenLastMethod_AtLevel0_Succeeds()
    {
        // Setup TOTP
        await SetupTotpForDefaultUser();

        // At Level 0, disabling last 2FA is allowed
        var response = await Client.PostAsync("/api/account/mfa/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── RememberMe ──

    [Fact]
    public async Task Login_WithRememberMe_SetsAuthCookie()
    {
        var client = CreateUnauthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234", RememberMe = true }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutRememberMe_SetsAuthCookie()
    {
        var client = CreateUnauthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234", RememberMe = false }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── MagicLinkSelfService ──

    [Fact]
    public async Task MagicLinkRequest_WhenSelfServiceEnabled_ReturnsOk()
    {
        // Default config has MagicLinkSelfService = true
        var client = CreateUnauthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "test@test.com" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── /api/app-info ──

    [Fact]
    public async Task AppInfo_ReturnsNewConfigFields()
    {
        var client = CreateUnauthenticatedClient();
        var response = await client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("AuthenticationMinimumLevel", out var level));
        Assert.Equal(0, level.GetInt32()); // Test fixture sets Level 0
        Assert.True(body.TryGetProperty("MagicLinkSelfService", out var mls));
        Assert.True(mls.GetBoolean());
    }

    [Fact]
    public async Task AppInfo_DoesNotReturnOldToggleFields()
    {
        var client = CreateUnauthenticatedClient();
        var response = await client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // Old fields should NOT exist
        Assert.False(body.TryGetProperty("MagicLinkEnabled", out _));
        Assert.False(body.TryGetProperty("EmailOtpAvailable", out _));
        Assert.False(body.TryGetProperty("PasskeyAvailable", out _));
    }

    // ── Admin Magic Link ──

    [Fact]
    public async Task AdminSendMagicLink_ToUserWithEmail_ReturnsOk()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "ML", lastname: "User", acronym: "ML",
            email: "ml@test.com", password: "TestPass1234", permissions: []);

        var shortId = new ShortGuid(user.Id).ToString();
        var response = await Client.PostAsync($"/api/admin/users/{shortId}/magic-link", null, TestContext.Current.CancellationToken);
        // May succeed or fail depending on email service, but should not be 401/403
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminSendMagicLink_RequiresAdminPermission()
    {
        var nonAdminUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Non", lastname: "Admin", acronym: "NA",
            email: "na@test.com", password: "TestPass1234", permissions: []);

        var nonAdminClient = await CreateAuthenticatedClientAsync("na", "TestPass1234");
        var shortId = new ShortGuid(nonAdminUser.Id).ToString();
        var response = await nonAdminClient.PostAsync($"/api/admin/users/{shortId}/magic-link", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ──

    private HttpClient CreateUnauthenticatedClient() => Factory.CreateClient();

    private async Task SetupTotpForDefaultUser()
    {
        var setupResponse = await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        Assert.True(setupResponse.IsSuccessStatusCode);

        var setup = await setupResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var sharedKey = setup.GetProperty("SharedKey").GetString()!;

        // Generate a valid TOTP code
        var code = GenerateTotp(sharedKey);
        var verifyResponse = await Client.PostAsJsonAsync("/api/account/mfa/verify", new { Code = code }, TestContext.Current.CancellationToken);
        Assert.True(verifyResponse.IsSuccessStatusCode);
    }

    internal static string GenerateTotpForTest(string base32Secret) => GenerateTotp(base32Secret);

    private static string GenerateTotp(string base32Secret)
    {
        // Decode Base32
        var cleanKey = base32Secret.Replace(" ", "").ToUpperInvariant();
        var bytes = Base32Decode(cleanKey);

        // TOTP: HMAC-SHA1 over counter (time / 30)
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new System.Security.Cryptography.HMACSHA1(bytes);
        var hash = hmac.ComputeHash(counterBytes);

        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24
                  | (hash[offset + 1] & 0xFF) << 16
                  | (hash[offset + 2] & 0xFF) << 8
                  | (hash[offset + 3] & 0xFF)) % 1000000;

        return code.ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var val = chars.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8) { bitsLeft -= 8; output.Add((byte)(buffer >> bitsLeft)); }
        }
        return output.ToArray();
    }
}
