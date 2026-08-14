using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Functions;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Domain.FunctionTerminals;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Functions;

/// <summary>
/// MG-FT-05 — the staffing ceremony (plan §12/§13): an enrolled terminal
/// begins a WebAuthn ceremony with its enrollment token, a person with an
/// ACTIVE activation grant taps their passkey, and the custom staffing grant
/// opens a <see cref="StaffingSession"/> whose token subject is the FUNCTION.
/// At most one active session per terminal — a second tap supersedes the
/// first (ReplacedByNewActivation) and cuts its tokens off. Everything off
/// the happy path is refused: wrong device key, suspended grant, replayed
/// ceremony, flag off.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FunctionStaffingTests : IntegrationTestBase
{
    public FunctionStaffingTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string RpId = "alerthub.localhost";
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string DeviceEndpoint = "http://localhost/connect/device";
    private const string TokenEndpoint = "http://localhost/connect/token";
    private const string BeginEndpoint = "http://localhost/connect/function-staffing/begin";

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.FunctionTerminals = enabled;

    [Fact]
    public async Task A_passkey_tap_opens_a_staffing_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-staff", ct);

        // Begin — the terminal asks for assertion options with its enrollment
        // token + a proof of its enrolled key.
        var begin = await BeginStaffingAsync(setup, ct);
        Assert.Single(begin.Options.GetProperty("allowCredentials").EnumerateArray());
        Assert.Equal(RpId, begin.Options.GetProperty("rpId").GetString());

        // Tap + redeem.
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");
        var tokenResp = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
        Assert.True(tokenResp.IsSuccessStatusCode, $"staffing grant failed ({(int)tokenResp.StatusCode}): {tokenBody}");
        using var tokens = JsonDocument.Parse(tokenBody);
        Assert.Equal("DPoP", tokens.RootElement.GetProperty("token_type").GetString());
        Assert.False(string.IsNullOrEmpty(tokens.RootElement.GetProperty("refresh_token").GetString()));

        // Session + terminal pointer + audit metadata.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var staffing = (await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
        Assert.Equal(StaffingSessionStatus.Active, staffing.Status);
        Assert.Equal(setup.UserId, staffing.ActivatedByUserId);
        Assert.Equal(setup.FunctionId, staffing.FunctionPrincipalId);
        Assert.Equal(setup.DeviceKey.Jkt, staffing.DpopJkt);
        Assert.False(string.IsNullOrEmpty(staffing.OAuthAuthorizationId));
        Assert.True(staffing.AbsoluteExpiresAt > DateTimeOffset.UtcNow.AddHours(15));

        var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
        Assert.Equal(staffing.Id, terminal!.ActiveStaffingSessionId);

        // Event-sourced: session stream = started; terminal stream gained the
        // activation event (created + enrolled + activated).
        Assert.Equal(1, (await session.Events.FetchStreamAsync(staffing.Id, token: ct)).Count);
        Assert.Equal(3, (await session.Events.FetchStreamAsync(setup.TerminalId, token: ct)).Count);

        // The ceremony is single-use — a replay of the same ceremony_id fails.
        var replay = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_second_tap_supersedes_the_active_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-supersede", ct);

        var firstTokens = await TapAsync(setup, ct, signCount: 1);
        var firstRefresh = firstTokens.RootElement.GetProperty("refresh_token").GetString()!;
        firstTokens.Dispose();

        // The verifier advances the credential's signature counter — the
        // second tap must present a higher count or it fails closed.
        var secondTokens = await TapAsync(setup, ct, signCount: 2);
        secondTokens.Dispose();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var sessions = (await session.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct))
                .OrderBy(s => s.StartedAt).ToList();
            Assert.Equal(2, sessions.Count);
            Assert.Equal(StaffingSessionStatus.Ended, sessions[0].Status);
            Assert.Equal(StaffingSessionEndReason.ReplacedByNewActivation, sessions[0].EndReason);
            Assert.Equal(StaffingSessionStatus.Active, sessions[1].Status);

            var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
            Assert.Equal(sessions[1].Id, terminal!.ActiveStaffingSessionId);
        }

        // The replaced session's authorization is revoked — its refresh chain
        // is dead, not just idle.
        var refreshResp = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = firstRefresh,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.False(refreshResp.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_exchange_refuses_a_wrong_device_key()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-wrongtap", ct);

        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");

        using var attackerKey = new DpopProofBuilder();
        var resp = await PostTokenAsync(StaffingForm(setup.ClientId, begin.CeremonyId, assertion), attackerKey);
        Assert.False(resp.IsSuccessStatusCode);
        Assert.Contains("enrolled key", await resp.Content.ReadAsStringAsync(ct));

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        Assert.Empty(await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
    }

    [Fact]
    public async Task A_suspended_grant_cannot_open_a_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-suspended", ct);

        // Ceremony begun while the grant was still active…
        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");

        // …then the grant is suspended before the tap is redeemed.
        var suspend = await Client.PostAsync(
            $"/api/function/{new ShortGuid(setup.FunctionId)}/grants/{setup.GrantId}/suspend", null, ct);
        Assert.True(suspend.IsSuccessStatusCode, await suspend.Content.ReadAsStringAsync(ct));

        var resp = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        Assert.False(resp.IsSuccessStatusCode);
        Assert.Contains("not authorized to staff", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Begin_requires_the_flag_and_an_enrollment_token()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-begin-gate", ct);

        // Anonymous → 401 (the OpenIddict validation scheme guards the route).
        var anon = Factory.CreateClient();
        var noToken = await anon.PostAsync("/connect/function-staffing/begin", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // Missing DPoP proof → refused even with a valid enrollment token.
        var proofless = new HttpRequestMessage(HttpMethod.Post, "/connect/function-staffing/begin");
        proofless.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        var prooflessResp = await Factory.CreateClient().SendAsync(proofless, ct);
        Assert.Equal(HttpStatusCode.Forbidden, prooflessResp.StatusCode);
        Assert.Contains("DPoP", await prooflessResp.Content.ReadAsStringAsync(ct));

        // Flag off → the surface does not exist.
        SetFeatureFlag(false);
        try
        {
            var dark = new HttpRequestMessage(HttpMethod.Post, "/connect/function-staffing/begin");
            dark.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
            dark.Headers.Add(DpopConstants.HeaderName,
                setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow));
            var darkResp = await Factory.CreateClient().SendAsync(dark, ct);
            Assert.Equal(HttpStatusCode.NotFound, darkResp.StatusCode);
        }
        finally
        {
            SetFeatureFlag(true);
        }
    }

    // ─── scenario setup ───────────────────────────────────────────────────

    private sealed record StaffingSetup(
        Guid FunctionId,
        Guid TerminalId,
        string ClientId,
        Guid UserId,
        string GrantId,
        string EnrollmentAccessToken,
        DpopProofBuilder DeviceKey,
        SoftwareWebAuthnAuthenticator Authenticator);

    /// <summary>Function (policy on) + granted user with a seeded RP-ID
    /// passkey + terminal slot enrolled via the full MG-FT-04 device flow.</summary>
    private async Task<StaffingSetup> SetUpEnrolledTerminalWithGrantedUserAsync(string accountName, CancellationToken ct)
    {
        // Function + terminal slot via the admin API.
        var fnResp = await Client.PostAsJsonAsync("/api/function", new
        {
            AccountName = accountName,
            TerminalPolicy = new { Enabled = true },
        }, JsonOptions, ct);
        Assert.True(fnResp.IsSuccessStatusCode, await fnResp.Content.ReadAsStringAsync(ct));
        var fnId = new ShortGuid((await fnResp.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct))!.Id).Guid;

        var termResp = await Client.PostAsJsonAsync($"/api/function/{new ShortGuid(fnId)}/terminals",
            new { DisplayName = "Staff-Terminal", Location = "Tor 1", WebAuthnRpId = RpId }, JsonOptions, ct);
        Assert.True(termResp.IsSuccessStatusCode, await termResp.Content.ReadAsStringAsync(ct));
        var terminal = (await termResp.Content.ReadFromJsonAsync<TerminalDto>(JsonOptions, ct))!;
        var terminalId = new ShortGuid(terminal.Id).Guid;

        // Granted user with a software passkey under the terminal's RP-ID.
        var userResp = await Client.PostAsJsonAsync("/api/user", new UserCreateDto
        {
            Firstname = "Staff",
            Lastname = accountName.ToUpperInvariant(),
            Acronym = $"S{Math.Abs(accountName.GetHashCode()) % 1000}",
            Email = $"{accountName}@staff.test",
            IsActive = true,
        }, JsonOptions, ct);
        Assert.True(userResp.IsSuccessStatusCode, await userResp.Content.ReadAsStringAsync(ct));
        var userId = new ShortGuid((await userResp.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct))!.Id!).Guid;

        var grantResp = await Client.PostAsJsonAsync($"/api/function/{new ShortGuid(fnId)}/grants",
            new { UserId = new ShortGuid(userId).ToString() }, JsonOptions, ct);
        Assert.True(grantResp.IsSuccessStatusCode, await grantResp.Content.ReadAsStringAsync(ct));
        var grantId = (await grantResp.Content.ReadFromJsonAsync<FunctionGrantDto>(JsonOptions, ct))!.Id;

        var authenticator = new SoftwareWebAuthnAuthenticator(userId.ToByteArray());
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new StoredPasskeyCredential
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CredentialId = authenticator.CredentialId,
                PublicKey = authenticator.CosePublicKey(),
                UserHandle = authenticator.UserHandle,
                SignatureCount = 0,
                AttestationType = "none",
                DisplayName = "Staffing test passkey",
                RpId = RpId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(ct);
        }

        // Enroll the terminal via the MG-FT-04 device flow.
        var deviceKey = new DpopProofBuilder();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(admin, userCode);
        var approve = await SubmitDecisionAsync(admin, userCode);
        Assert.True((int)approve.StatusCode < 400,
            $"approve failed ({(int)approve.StatusCode}): {await approve.Content.ReadAsStringAsync(ct)}");
        var poll = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = DeviceCodeGrant,
            ["device_code"] = deviceCode,
            ["client_id"] = terminal.ClientId,
        }, deviceKey);
        var pollBody = await poll.Content.ReadAsStringAsync(ct);
        Assert.True(poll.IsSuccessStatusCode, $"enrollment poll failed ({(int)poll.StatusCode}): {pollBody}");
        using var tokens = JsonDocument.Parse(pollBody);
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;

        return new StaffingSetup(fnId, terminalId, terminal.ClientId, userId, grantId,
            accessToken, deviceKey, authenticator);
    }

    // ─── flow helpers ─────────────────────────────────────────────────────

    private sealed record BeginResult(string CeremonyId, JsonElement Options);

    private async Task<BeginResult> BeginStaffingAsync(StaffingSetup setup, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/function-staffing/begin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        request.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow));
        var resp = await Factory.CreateClient().SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"staffing begin failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return new BeginResult(
            doc.RootElement.GetProperty("ceremonyId").GetString()!,
            doc.RootElement.GetProperty("publicKey").Clone());
    }

    private Task<HttpResponseMessage> RedeemStaffingAsync(StaffingSetup setup, string ceremonyId, string assertion) =>
        PostTokenAsync(StaffingForm(setup.ClientId, ceremonyId, assertion), setup.DeviceKey);

    private static Dictionary<string, string> StaffingForm(string clientId, string ceremonyId, string assertion) => new()
    {
        ["grant_type"] = FunctionGrantTypes.StaffingSession,
        ["client_id"] = clientId,
        ["ceremony_id"] = ceremonyId,
        ["assertion"] = assertion,
    };

    /// <summary>Begin + tap + redeem in one step; returns the parsed token response.</summary>
    private async Task<JsonDocument> TapAsync(StaffingSetup setup, CancellationToken ct, uint signCount = 1)
    {
        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount);
        var resp = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"staffing grant failed ({(int)resp.StatusCode}): {body}");
        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form, DpopProofBuilder key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add(DpopConstants.HeaderName, key.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        return await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodeAsync(string clientId, string dpopProof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/device")
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("client_id", clientId),
            }),
        };
        request.Headers.Add(DpopConstants.HeaderName, dpopProof);
        var resp = await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/device failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("device_code").GetString()!,
            doc.RootElement.GetProperty("user_code").GetString()!);
    }

    private async Task OpenVerificationAsync(HttpClient cookieClient, string userCode)
    {
        var resp = await cookieClient.GetAsync(
            $"/connect/verify?user_code={Uri.EscapeDataString(userCode)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
    }

    private Task<HttpResponseMessage> SubmitDecisionAsync(HttpClient cookieClient, string userCode) =>
        cookieClient.PostAsync("/connect/verify", new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("user_code", userCode),
            new("decision", "approve"),
        }), TestContext.Current.CancellationToken);

    // Minimal ES256 DPoP proof factory — same shape as the enrollment tests'.
    internal sealed class DpopProofBuilder : IDisposable
    {
        private static readonly JsonSerializerOptions ProofJson =
            new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Jkt { get; }

        public DpopProofBuilder()
        {
            var p = _ec.ExportParameters(false);
            Jkt = JwkThumbprint.ForEc("P-256", p.Q.X!, p.Q.Y!);
        }

        public string CreateProof(string htm, string htu, DateTimeOffset iat)
        {
            var p = _ec.ExportParameters(false);
            var jwk = new { kty = "EC", crv = "P-256", x = B64(p.Q.X!), y = B64(p.Q.Y!) };
            var header = new { typ = "dpop+jwt", alg = "ES256", jwk };
            var payload = new Dictionary<string, object>
            {
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["htm"] = htm,
                ["htu"] = htu,
                ["iat"] = iat.ToUnixTimeSeconds(),
            };
            var signingInput = $"{Seg(header)}.{Seg(payload)}";
            var sig = _ec.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{signingInput}.{B64(sig)}";
        }

        private static string Seg(object o) => B64(JsonSerializer.SerializeToUtf8Bytes(o, ProofJson));
        private static string B64(byte[] b) => Base64Url.EncodeToString(b);

        public void Dispose() => _ec.Dispose();
    }
}
