using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 2 of the "similar bugs" remediation (lifecycle-gate bypass on 2FA completion).
/// Findings #13/#14: the 2FA-completion endpoints minted a full, kill-switch-surviving
/// session without re-checking IsActive/IsDeleted. The partial 2FA cookie is not
/// stamp-validated, so a user deactivated AFTER the password step but before the 2FA
/// step could still complete TOTP / email-OTP and obtain a durable session.
/// Both endpoints already resolve the user via GetTwoFactorAuthenticationUserAsync —
/// the fix re-checks account state there and rejects before sign-in.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class SecurityAuditWave2Tests : IntegrationTestBase
{
    private const string Password = "TestPass1234";

    public SecurityAuditWave2Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #14 — TOTP completion must reject a user deactivated mid-login.
    [Fact]
    public async Task TotpCompletion_RejectsUserDeactivatedMidLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Totp", lastname: "Gate", acronym: "TT", email: "totp-gate@test.com", password: Password);

        // Enable TOTP for the user (setup + verify on their own session).
        var uc = await CreateAuthenticatedClientAsync("tt", Password);
        await uc.PostAsync("/api/account/mfa/setup", null, ct);
        var setupCode = await GenerateTotpAsync(user.Id);
        Assert.Equal(HttpStatusCode.OK,
            (await uc.PostAsJsonAsync("/api/account/mfa/verify", new { Code = setupCode }, ct)).StatusCode);

        // Fresh password login → partial 2FA cookie.
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        Assert.Equal(HttpStatusCode.OK,
            (await loginClient.PostAsJsonAsync("/api/account/login", new { UserName = "tt", Password = Password }, ct)).StatusCode);

        // Deactivated mid-login (after the partial cookie was issued).
        await DeactivateAsync(user.Id, ct);

        // Submitting a valid TOTP must now be rejected. RED today: completes sign-in.
        var code = await GenerateTotpAsync(user.Id);
        var mfa = await loginClient.PostAsJsonAsync("/api/account/mfa/login", new { Code = code }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, mfa.StatusCode);
    }

    // #13 — Email-OTP completion must reject a user deactivated mid-login.
    [Fact]
    public async Task EmailOtpCompletion_RejectsUserDeactivatedMidLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Eotp", lastname: "Gate", acronym: "EO", email: "eotp-gate@test.com", password: Password);

        var uc = await CreateAuthenticatedClientAsync("eo", Password);
        await uc.PostAsync("/api/account/email-otp/enable", null, ct);

        var emailSvc = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailSvc.Clear();

        // Fresh password login → partial 2FA cookie (RequiresMfa: email).
        var loginClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await loginClient.PostAsJsonAsync("/api/account/login", new { UserName = "eo", Password = Password }, ct);

        // Request the OTP (sends the email), then extract the code.
        Assert.Equal(HttpStatusCode.OK,
            (await loginClient.PostAsync("/api/account/email-otp/login/request", null, ct)).StatusCode);
        var code = ExtractOtp(emailSvc, "eotp-gate@test.com");
        Assert.NotNull(code);

        // Deactivated mid-login.
        await DeactivateAsync(user.Id, ct);

        // Submitting the valid OTP must now be rejected. RED today: completes sign-in.
        var resp = await loginClient.PostAsJsonAsync("/api/account/email-otp/login", new { Code = code }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task DeactivateAsync(Guid userId, CancellationToken ct)
    {
        await using var s = GetTenantedDocumentSession();
        var au = await s.LoadAsync<ApplicationUser>(userId, ct);
        au!.IsActive = false;
        s.Store(au);
        await s.SaveChangesAsync(ct);
    }

    private static string? ExtractOtp(InMemoryEmailService emailSvc, string to)
    {
        var email = emailSvc.GetLastEmailTo(to);
        if (email is null) return null;
        var m = OtpRegex().Match(email.HtmlBody);
        return m.Success ? m.Groups[1].Value : null;
    }

    private async Task<string> GenerateTotpAsync(Guid userId)
    {
        var securityData = await Factory.GetDocumentAsync<UserSecurityData>(userId);
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

    [GeneratedRegex(@"(\d{6})", RegexOptions.None)]
    private static partial Regex OtpRegex();
}
