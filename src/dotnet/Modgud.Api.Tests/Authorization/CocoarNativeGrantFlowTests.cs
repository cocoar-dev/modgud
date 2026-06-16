using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end verification of the ADR-0010 Phase-1 native (cookieless)
/// passwordless token grants at <c>/connect/token</c>:
/// <c>urn:cocoar:otp</c> and <c>urn:cocoar:magic</c>. Covers the happy paths
/// (factor → minted RS256 tokens, short access TTL, no auth cookie), the two
/// gates (per-realm <c>NativeGrants.Enabled</c> flag, per-client
/// <c>gt:urn:cocoar:*</c> permission), anti-enumeration on bad proofs, and the
/// optional <c>totp_code</c> second-factor path.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class CocoarNativeGrantFlowTests : IntegrationTestBase
{
    public CocoarNativeGrantFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string TestEmail = "test@test.com";

    // ─────────────────────────────── OTP grant ────────────────────────────────

    [Fact]
    public async Task Otp_Grant_MintsTokens_ShortLifetime_NoCookie()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-otp-app");

        var code = await RequestNativeOtpCodeAsync();

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-otp-app",
            ["client_secret"] = "native-otp-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["scope"] = "openid email profile offline_access",
        });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"/connect/token failed ({(int)response.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        Assert.False(string.IsNullOrEmpty(accessToken));
        Assert.True(json.RootElement.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()),
            "expected a (reference) refresh_token because offline_access was requested");

        // ADR-0010 — native access tokens are short-lived JWTs.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        Assert.Equal(DefaultUser!.Id.ToString(), jwt.Subject);
        var iat = long.Parse(jwt.Payload["iat"].ToString()!);
        var exp = long.Parse(jwt.Payload["exp"].ToString()!);
        var lifetimeMinutes = (exp - iat) / 60.0;
        Assert.InRange(lifetimeMinutes, 14, 16);

        // Cookieless guarantee — the token endpoint must not set an auth cookie.
        Assert.False(response.Headers.Contains("Set-Cookie"),
            "the native grant must mint tokens without setting any cookie");
    }

    [Fact]
    public async Task Otp_Grant_RealmFlagOff_Rejected()
    {
        // NativeGrants left at its default (OFF) — do NOT enable.
        await SeedNativeClientAsync("native-otp-app");
        // Seed a challenge directly via the service so the only thing under test
        // is the realm gate (the request endpoint would also be gated off).
        var code = await IssueNativeOtpViaServiceAsync(DefaultUser!.Id);

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-otp-app",
            ["client_secret"] = "native-otp-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["scope"] = "openid",
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("unsupported_grant_type", body);
    }

    [Fact]
    public async Task Otp_Grant_ClientWithoutPermission_Rejected()
    {
        await EnableNativeGrantsAsync();
        // Client that only carries the standard grants — no gt:urn:cocoar:* permission.
        await SeedClientAsync("plain-app", ["authorization_code", "refresh_token"]);
        // Issue the code via the service (the HTTP request endpoint shares a
        // per-IP rate-limit budget across the integration collection; only the
        // one end-to-end happy-path test drives it).
        var code = await IssueNativeOtpViaServiceAsync(DefaultUser!.Id);

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "plain-app",
            ["client_secret"] = "plain-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["scope"] = "openid",
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // OpenIddict rejects the un-permitted grant before the branch runs.
        Assert.Contains("unauthorized_client", body);
    }

    [Fact]
    public async Task Otp_Grant_WrongCode_InvalidGrant_Uniform()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-otp-app");
        await IssueNativeOtpViaServiceAsync(DefaultUser!.Id); // a real challenge exists

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-otp-app",
            ["client_secret"] = "native-otp-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = "000000",
            ["scope"] = "openid",
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
    }

    [Fact]
    public async Task Otp_Grant_UnknownEmail_InvalidGrant_NoOracle()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-otp-app");

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-otp-app",
            ["client_secret"] = "native-otp-app-secret",
            ["username"] = "nobody@nowhere.example",
            ["otp_code"] = "123456",
            ["scope"] = "openid",
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Same invalid_grant as a wrong code — no user-existence oracle.
        Assert.Contains("invalid_grant", body);
    }

    // ─────────────────────────── Native OTP request ───────────────────────────

    [Fact]
    public async Task NativeOtpRequest_UnknownEmail_UniformResponse_NoSend()
    {
        await EnableNativeGrantsAsync();
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var anon = Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/account/native/otp/request",
            new { Email = "nobody@nowhere.example" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(emailService.GetLastEmailTo("nobody@nowhere.example"));
    }

    [Fact]
    public async Task NativeOtpRequest_RealmFlagOff_NoSend()
    {
        // Flag left OFF.
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var anon = Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/account/native/otp/request",
            new { Email = TestEmail }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(emailService.GetLastEmailTo(TestEmail));
    }

    // ────────────────────────────── Magic grant ───────────────────────────────

    [Fact]
    public async Task Magic_Grant_MintsTokens()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-magic-app");

        var (userId, token) = await SeedMagicLinkAsync(DefaultUser!.Id);

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Magic,
            ["client_id"] = "native-magic-app",
            ["client_secret"] = "native-magic-app-secret",
            ["user_id"] = userId,
            ["magic_token"] = token,
            ["scope"] = "openid email profile offline_access",
        });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"/connect/token failed ({(int)response.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("access_token").GetString()));
        Assert.True(json.RootElement.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Magic_Grant_TokenReuse_Rejected()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-magic-app");

        var (userId, token) = await SeedMagicLinkAsync(DefaultUser!.Id);

        var first = await PostMagicAsync(userId, token);
        Assert.True(first.IsSuccessStatusCode);

        // Single-use: the same link must not redeem twice.
        var second = await PostMagicAsync(userId, token);
        Assert.False(second.IsSuccessStatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
    }

    // ─────────────────────── Settings lifetime validation ─────────────────────

    [Fact]
    public async Task NativeGrants_OutOfBandLifetimes_Rejected()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();

        // Over-long access TTL would mint an effectively-permanent non-revocable JWT.
        var tooLong = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true, AccessTokenLifetimeMinutes = 525_600 },
        }, TestContext.Current.CancellationToken);
        Assert.True(tooLong.IsError);

        var zero = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true, AccessTokenLifetimeMinutes = 0 },
        }, TestContext.Current.CancellationToken);
        Assert.True(zero.IsError);

        var badRefresh = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true, RefreshTokenLifetimeDays = 9_999 },
        }, TestContext.Current.CancellationToken);
        Assert.True(badRefresh.IsError);

        // The sane defaults (Enabled only) must still patch cleanly.
        var ok = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
        Assert.False(ok.IsError);
    }

    // ─────────────────────────────── 2FA path ─────────────────────────────────

    [Fact]
    public async Task Otp_Grant_TwoFactorUser_WithValidTotp_Succeeds()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-otp-app");
        await EnableTwoFactorAsync();

        var code = await IssueNativeOtpViaServiceAsync(DefaultUser!.Id);
        var totp = await GenerateValidTotpCodeAsync();

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-otp-app",
            ["client_secret"] = "native-otp-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["totp_code"] = totp,
            ["scope"] = "openid",
        });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"/connect/token failed ({(int)response.StatusCode}): {body}");
    }

    [Fact]
    public async Task Otp_Grant_TwoFactorUser_WithoutTotp_Rejected()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-otp-app");
        await EnableTwoFactorAsync();

        var code = await IssueNativeOtpViaServiceAsync(DefaultUser!.Id);

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-otp-app",
            ["client_secret"] = "native-otp-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["scope"] = "openid",
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
    }

    // ─────────────────────────────── Helpers ──────────────────────────────────

    private Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form) =>
        Factory.CreateClient().PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> PostMagicAsync(string userId, string token) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Magic,
            ["client_id"] = "native-magic-app",
            ["client_secret"] = "native-magic-app-secret",
            ["user_id"] = userId,
            ["magic_token"] = token,
            ["scope"] = "openid",
        });

    private async Task EnableNativeGrantsAsync()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Drives the public native OTP-request endpoint and scrapes the
    /// 6-digit code out of the captured email — the real native client flow.</summary>
    private async Task<string> RequestNativeOtpCodeAsync()
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var anon = Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/account/native/otp/request",
            new { Email = TestEmail }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var email = emailService.GetLastEmailTo(TestEmail);
        Assert.NotNull(email);
        var match = OtpCodeRegex().Match(email!.HtmlBody);
        Assert.True(match.Success, "no 6-digit OTP code found in the captured email");
        return match.Groups[1].Value;
    }

    /// <summary>Issues an OTP challenge directly via the service (bypasses the
    /// HTTP endpoint's own realm gate) so a test can target only the grant gate.</summary>
    private async Task<string> IssueNativeOtpViaServiceAsync(Guid userId)
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        using var scope = NewSystemTenantScope();
        var otp = scope.ServiceProvider.GetRequiredService<Modgud.Authentication.Identity.IEmailOtpService>();
        var result = await otp.RequestNativeOtpAsync(userId, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, "RequestNativeOtpAsync failed to issue a challenge");
        var email = emailService.GetLastEmailTo(TestEmail);
        Assert.NotNull(email);
        return OtpCodeRegex().Match(email!.HtmlBody).Groups[1].Value;
    }

    /// <summary>Seeds a <see cref="MagicLinkChallenge"/> directly, replicating
    /// exactly what the request endpoint stores (opaque 256-bit token, SHA-256
    /// hash at rest). The grant tests deliberately do NOT drive the shared
    /// <c>/api/account/magic-link/request</c> endpoint: its per-IP rate-limit
    /// budget is shared across the magic-link test classes in this collection, so
    /// driving it here flakes under a full-suite run. The request→email→link path
    /// itself is covered by MagicLinkTests.</summary>
    private async Task<(string UserId, string Token)> SeedMagicLinkAsync(Guid userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new MagicLinkChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = MagicLinkChallenge.HashToken(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (userId.ToString(), token);
    }

    private async Task EnableTwoFactorAsync()
    {
        await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        var code = await GenerateValidTotpCodeAsync();
        var resp = await Client.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = code }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<string> GenerateValidTotpCodeAsync()
    {
        var securityData = await Factory.GetDocumentAsync<UserSecurityData>(DefaultUser!.Id);
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
            var value = alphabet.IndexOf(input[inputIndex]);
            if (value < 0) { inputIndex++; continue; }
            if (bitIndex <= 3)
            {
                bitIndex = (bitIndex + 5) % 8;
                if (bitIndex == 0)
                {
                    output[outputIndex] |= (byte)value;
                    outputIndex++;
                    outputBits = 0;
                }
                else
                {
                    output[outputIndex] |= (byte)(value << (8 - bitIndex));
                    outputBits = bitIndex;
                }
            }
            else
            {
                bitIndex = (bitIndex + 5) % 8;
                output[outputIndex] |= (byte)(value >> bitIndex);
                outputIndex++;
                output[outputIndex] |= (byte)(value << (8 - bitIndex));
                outputBits = bitIndex;
            }
            inputIndex++;
        }
        return output;
    }

    // ── Seeding ────────────────────────────────────────────────────────────

    private Task SeedNativeClientAsync(string clientId) =>
        SeedClientAsync(clientId, [CocoarGrantTypes.Otp, CocoarGrantTypes.Magic, "refresh_token"]);

    private async Task SeedClientAsync(string clientId, List<string> grantTypes)
    {
        var app = await CreateAppAsync($"{clientId}-catalog", clientId);

        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"{clientId}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "email", "profile", "offline_access"],
            AllowedGrantTypes = grantTypes,
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [new ShortGuid(app.Id).ToString()],
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task<App> CreateAppAsync(string slug, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: displayName, Description: null,
            Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }

    [GeneratedRegex(@"(\d{6})", RegexOptions.None)]
    private static partial Regex OtpCodeRegex();
}
