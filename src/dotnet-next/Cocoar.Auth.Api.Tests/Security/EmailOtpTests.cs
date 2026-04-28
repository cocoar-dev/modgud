using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Infrastructure.Email;

namespace Cocoar.Auth.Api.Tests.Security;

/// <summary>
/// Tests for Email OTP MFA flow.
/// Verifies enable/disable, login flow, rate limiting, and security properties.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class EmailOtpTests : IntegrationTestBase
{
    public EmailOtpTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ─── Status & Enable/Disable ────────────────────────────────────────

    [Fact]
    public async Task EmailOtpStatus_ReturnsDisabledByDefault()
    {
        var response = await Client.GetAsync("/api/account/email-otp/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(status.GetProperty("Enabled").GetBoolean());
        Assert.True(status.GetProperty("HasEmail").GetBoolean());
    }

    [Fact]
    public async Task EmailOtpEnable_SetsEnabledFlag()
    {
        var response = await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var statusResponse = await Client.GetAsync("/api/account/email-otp/status", TestContext.Current.CancellationToken);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(status.GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public async Task EmailOtpDisable_ClearsEnabledFlag()
    {
        await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        var response = await Client.PostAsync("/api/account/email-otp/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var statusResponse = await Client.GetAsync("/api/account/email-otp/status", TestContext.Current.CancellationToken);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(status.GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public async Task EmailOtpEndpoints_RequireAuthentication()
    {
        var anonClient = Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonClient.GetAsync("/api/account/email-otp/status", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonClient.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonClient.PostAsync("/api/account/email-otp/disable", null, TestContext.Current.CancellationToken)).StatusCode);
    }

    // ─── Login Flow ─────────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithEmailOtpEnabled_RequiresMfa()
    {
        await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        var loginResponse = await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        Assert.True(loginResponse.IsSuccessStatusCode);
        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("RequiresMfa").GetBoolean());

        var methods = body.GetProperty("MfaMethods");
        Assert.Contains("email", methods.EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task EmailOtpLogin_FullFlow_CompletesSignIn()
    {
        await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        // Step 1: Password login → RequiresMfa
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        // Step 2: Request OTP → email sent
        var requestResponse = await loginClient.PostAsync("/api/account/email-otp/login/request", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, requestResponse.StatusCode);

        // Step 3: Extract code from InMemoryEmailService
        var code = ExtractOtpCodeFromEmail();
        Assert.NotNull(code);

        // Step 4: Login with code
        var otpResponse = await loginClient.PostAsJsonAsync("/api/account/email-otp/login",
            new { Code = code }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, otpResponse.StatusCode);

        // Step 5: Verify full auth
        var meResponse = await loginClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task EmailOtpLogin_WithInvalidCode_Fails()
    {
        await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);
        await loginClient.PostAsync("/api/account/email-otp/login/request", null, TestContext.Current.CancellationToken);

        var response = await loginClient.PostAsJsonAsync("/api/account/email-otp/login",
            new { Code = "000000" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EmailOtpLogin_WithoutPasswordStep_Fails()
    {
        var anonClient = Factory.CreateClient();
        var response = await anonClient.PostAsync("/api/account/email-otp/login/request", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmailOtpLogin_ThreeWrongAttempts_BlocksFurther()
    {
        await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);
        await loginClient.PostAsync("/api/account/email-otp/login/request", null, TestContext.Current.CancellationToken);

        // 3 wrong attempts
        for (var i = 0; i < 3; i++)
        {
            await loginClient.PostAsJsonAsync("/api/account/email-otp/login",
                new { Code = "000000" }, TestContext.Current.CancellationToken);
        }

        // 4th attempt with correct code should still fail (challenge deleted after max attempts)
        var code = ExtractOtpCodeFromEmail();
        var response = await loginClient.PostAsJsonAsync("/api/account/email-otp/login",
            new { Code = code }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithBothTotpAndEmailOtp_ReturnsBothMethods()
    {
        // Enable TOTP
        await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        var totpCode = await GenerateValidTotpCodeAsync();
        await Client.PostAsJsonAsync("/api/account/mfa/verify", new { Code = totpCode }, TestContext.Current.CancellationToken);

        // Enable Email OTP
        await Client.PostAsync("/api/account/email-otp/enable", null, TestContext.Current.CancellationToken);
        await Client.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);

        // Login → should get both methods
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        var loginResponse = await loginClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var methods = body.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("totp", methods);
        Assert.Contains("email", methods);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private string? ExtractOtpCodeFromEmail()
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        var email = emailService.GetLastEmailTo("test@test.com");
        if (email is null) return null;

        // Extract 6-digit code from HTML body
        var match = OtpCodeRegex().Match(email.HtmlBody);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(\d{6})", RegexOptions.None)]
    private static partial Regex OtpCodeRegex();

    private async Task<string> GenerateValidTotpCodeAsync()
    {
        var securityData = await Factory.GetDocumentAsync<Cocoar.Auth.Authentication.Domain.UserSecurityData>(DefaultUser!.Id);
        Assert.NotNull(securityData?.AuthenticatorKey);

        var key = Base32Decode(securityData!.AuthenticatorKey!);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var timestampBytes = BitConverter.GetBytes(timestamp);
        if (BitConverter.IsLittleEndian) Array.Reverse(timestampBytes);

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
