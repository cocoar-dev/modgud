using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Api.Tests.Infrastructure;

namespace Cocoar.Auth.Api.Tests.Security;

/// <summary>
/// Tests for Multi-Factor Authentication (TOTP) flow.
/// Verifies setup, login, and security properties of the MFA implementation.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class MfaTests : IntegrationTestBase
{
    public MfaTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task MfaStatus_ReturnsDisabledByDefault()
    {
        var response = await Client.GetAsync("/api/account/mfa/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(status.GetProperty("Enabled").GetBoolean());
        Assert.False(status.GetProperty("HasAuthenticator").GetBoolean());
    }

    [Fact]
    public async Task MfaSetup_ReturnsSharedKeyAndAuthenticatorUri()
    {
        var response = await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var setup = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var sharedKey = setup.GetProperty("SharedKey").GetString();
        var uri = setup.GetProperty("AuthenticatorUri").GetString();

        Assert.NotNull(sharedKey);
        Assert.NotEmpty(sharedKey!);
        Assert.Contains("otpauth://totp/", uri);
        Assert.Contains("Cocoar.Auth", uri);
    }

    [Fact]
    public async Task MfaVerify_WithInvalidCode_ReturnsBadRequest()
    {
        // Setup first
        await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);

        // Try to verify with invalid code
        var response = await Client.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = "000000" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MfaVerify_WithValidCode_EnablesMfa()
    {
        // Setup
        await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);

        // Generate a valid TOTP code using the authenticator key
        var code = await GenerateValidTotpCodeAsync();

        // Verify
        var response = await Client.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = code }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Check status
        var statusResponse = await Client.GetAsync("/api/account/mfa/status", TestContext.Current.CancellationToken);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(status.GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public async Task MfaLogin_AfterEnabling_RequiresTwoFactor()
    {
        // Setup and enable MFA for default user
        await EnableMfaForDefaultUserAsync();

        // Logout
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        // Login with password only — should get RequiresMfa
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        var loginResponse = await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.True(loginResponse.IsSuccessStatusCode,
            $"Login failed: {loginResponse.StatusCode} — {loginBody}");
        var body = JsonSerializer.Deserialize<JsonElement>(loginBody);
        Assert.True(body.GetProperty("RequiresMfa").GetBoolean());

        // Should NOT be able to access protected endpoints yet
        var meResponse = await loginClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }

    [Fact]
    public async Task MfaLogin_WithValidCode_CompletesSignIn()
    {
        // Setup and enable MFA
        await EnableMfaForDefaultUserAsync();
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        // Step 1: password login
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        // Step 2: TOTP login
        var code = await GenerateValidTotpCodeAsync();
        var mfaResponse = await loginClient.PostAsJsonAsync("/api/account/mfa/login",
            new { Code = code }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mfaResponse.StatusCode);

        // Should now be able to access protected endpoints
        var meResponse = await loginClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task MfaLogin_WithInvalidCode_Fails()
    {
        // Setup and enable MFA
        await EnableMfaForDefaultUserAsync();
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        // Step 1: password login
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        // Step 2: wrong TOTP code
        var mfaResponse = await loginClient.PostAsJsonAsync("/api/account/mfa/login",
            new { Code = "000000" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, mfaResponse.StatusCode);
    }

    [Fact]
    public async Task MfaDisable_RemovesMfaRequirement()
    {
        // Enable MFA
        await EnableMfaForDefaultUserAsync();

        // Disable MFA
        var disableResponse = await Client.PostAsync("/api/account/mfa/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        // Logout and login — should NOT require MFA anymore
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        var loginResponse = await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        // Should have "Message" (success), not "RequiresMfa"
        Assert.False(body.TryGetProperty("RequiresMfa", out _));
    }

    [Fact]
    public async Task MfaEndpoints_RequireAuthentication()
    {
        var anonClient = Factory.CreateClient();

        var statusResponse = await anonClient.GetAsync("/api/account/mfa/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, statusResponse.StatusCode);

        var setupResponse = await anonClient.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, setupResponse.StatusCode);

        var verifyResponse = await anonClient.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = "123456" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, verifyResponse.StatusCode);

        var disableResponse = await anonClient.PostAsync("/api/account/mfa/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, disableResponse.StatusCode);
    }

    [Fact]
    public async Task MfaLogin_WithoutPasswordStep_Fails()
    {
        // Try to call mfa/login without having done the password step first
        var anonClient = Factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/account/mfa/login",
            new { Code = "123456" }, TestContext.Current.CancellationToken);

        // Should fail — no partial sign-in cookie exists
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task EnableMfaForDefaultUserAsync()
    {
        await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        var code = await GenerateValidTotpCodeAsync();
        var response = await Client.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = code }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Generates a valid TOTP code for the default test user by reading the
    /// authenticator key from the database and computing the current TOTP value.
    /// </summary>
    private async Task<string> GenerateValidTotpCodeAsync()
    {
        // Get the authenticator key from the database
        var securityData = await Factory.GetDocumentAsync<Cocoar.Auth.Authentication.Domain.UserSecurityData>(DefaultUser!.Id);
        Assert.NotNull(securityData?.AuthenticatorKey);

        // Compute TOTP using the same algorithm as authenticator apps
        var key = Base32Decode(securityData!.AuthenticatorKey!);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var timestampBytes = BitConverter.GetBytes(timestamp);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timestampBytes);

        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(timestampBytes);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24
                  | (hash[offset + 1] & 0xFF) << 16
                  | (hash[offset + 2] & 0xFF) << 8
                  | (hash[offset + 3] & 0xFF)) % 1_000_000;

        return code.ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new byte[input.Length * 5 / 8];
        var bitIndex = 0;
        var inputIndex = 0;
        var outputBits = 0;
        var outputIndex = 0;

        while (inputIndex < input.Length)
        {
            var byteIndex = alphabet.IndexOf(input[inputIndex]);
            if (byteIndex < 0) throw new FormatException($"Invalid Base32 character: {input[inputIndex]}");
            outputBits = (outputBits << 5) | byteIndex;
            bitIndex += 5;
            if (bitIndex >= 8)
            {
                output[outputIndex++] = (byte)(outputBits >> (bitIndex - 8));
                bitIndex -= 8;
            }
            inputIndex++;
        }
        return output;
    }
}
