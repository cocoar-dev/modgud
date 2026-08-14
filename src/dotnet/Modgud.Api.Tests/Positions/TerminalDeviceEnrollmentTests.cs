using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Positions;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// MG-FT-04 — terminal enrollment over the DPoP-bound device flow (plan §11):
/// a Pending slot's client starts a device-authorization request with a DPoP
/// proof, an admin holding <c>position-terminal:enroll</c> sees the TERMINAL
/// consent (position, slot, key fingerprint) and approves, and the device's
/// poll pins its key onto the slot (Pending → Active, Enrolled event) and
/// yields an enrollment token chain (terminal-control audience only, plus a
/// refresh token). Everything that deviates from that one path is refused:
/// missing permission, unbound request, wrong key, non-Pending slot, feature
/// flag off.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TerminalDeviceEnrollmentTests : IntegrationTestBase
{
    public TerminalDeviceEnrollmentTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string RpId = "alerthub.localhost";
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string DeviceEndpoint = "http://localhost/connect/device";
    private const string TokenEndpoint = "http://localhost/connect/token";

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    [Fact]
    public async Task A_pending_slot_enrolls_via_the_dpop_bound_device_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-enroll", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Tor links", ct);

        using var deviceKey = new DpopProofBuilder();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));

        // The hosted verification shows the TERMINAL consent, not scopes.
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        var ticket = await OpenVerificationAsync(admin, userCode);
        var info = await GetVerificationInfoAsync(admin, ticket, ct);
        Assert.Equal("ready", info.GetProperty("Status").GetString());
        Assert.Equal("terminal", info.GetProperty("Kind").GetString());
        var consent = info.GetProperty("Terminal");
        Assert.Equal("fn-enroll", consent.GetProperty("PositionName").GetString());
        Assert.Equal("Tor links", consent.GetProperty("TerminalName").GetString());
        Assert.Equal(terminal.ClientId, consent.GetProperty("ClientId").GetString());
        // Key fingerprint from the DPoP binding, XXXX-XXXX for the human check.
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", consent.GetProperty("DpopFingerprint").GetString());

        var approve = await SubmitDecisionAsync(admin, userCode, approve: true);
        Assert.True((int)approve.StatusCode < 400,
            $"approve failed ({(int)approve.StatusCode}): {await approve.Content.ReadAsStringAsync(ct)}");

        // The device's poll completes the enrollment.
        var poll = await PollTokenAsync(terminal.ClientId, deviceCode,
            deviceKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        var pollBody = await poll.Content.ReadAsStringAsync(ct);
        Assert.True(poll.IsSuccessStatusCode, $"/connect/token failed ({(int)poll.StatusCode}): {pollBody}");
        using var tokens = JsonDocument.Parse(pollBody);
        Assert.Equal("DPoP", tokens.RootElement.GetProperty("token_type").GetString());
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrEmpty(refreshToken));

        // Slot state: Active, the device key pinned, anchored on an authorization.
        var slot = await LoadSlotAsync(terminal.Id, ct);
        Assert.Equal(TerminalEnrollmentStatus.Active, slot.Status);
        Assert.Equal(deviceKey.Jkt, slot.DpopJkt);
        Assert.NotNull(slot.EnrolledAt);
        Assert.False(string.IsNullOrEmpty(slot.EnrollmentAuthorizationId));

        // Event-sourced: created + enrolled, nothing else.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stream = await session.Events.FetchStreamAsync(new ShortGuid(terminal.Id).Guid, token: ct);
            Assert.Equal(2, stream.Count);

            // The approval left its audit record (who approved which slot).
            var audits = await session.Query<TerminalEnrollmentVerificationTicket>()
                .Where(t => t.TerminalEnrollmentId == new ShortGuid(terminal.Id).Guid)
                .ToListAsync(ct);
            var audit = Assert.Single(audits);
            Assert.NotNull(audit.ConsumedAt);
            Assert.NotEqual(Guid.Empty, audit.ApprovingAdminUserId);
        }

        // The enrollment chain refreshes with the SAME key.
        var refresh = await RefreshTokenAsync(terminal.ClientId, refreshToken!,
            deviceKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        var refreshBody = await refresh.Content.ReadAsStringAsync(ct);
        Assert.True(refresh.IsSuccessStatusCode, $"refresh failed ({(int)refresh.StatusCode}): {refreshBody}");
        using var refreshed = JsonDocument.Parse(refreshBody);
        Assert.Equal("DPoP", refreshed.RootElement.GetProperty("token_type").GetString());
        Assert.False(string.IsNullOrEmpty(refreshed.RootElement.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Approval_is_refused_without_the_enroll_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-noperm", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Unbefugt", ct);

        using var deviceKey = new DpopProofBuilder();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));

        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Zero", lastname: "Enroll", acronym: "ZE", email: "zeroenroll@test.com", password: "TestPass1234");
        var zero = await CreateAuthenticatedClientAsync("ze", "TestPass1234");
        await OpenVerificationAsync(zero, userCode);

        var approve = await SubmitDecisionAsync(zero, userCode, approve: true);
        Assert.True((int)approve.StatusCode >= 400 ||
            (await approve.Content.ReadAsStringAsync(ct)).Contains("not authorized to enroll"));

        // Nothing was pinned.
        var slot = await LoadSlotAsync(terminal.Id, ct);
        Assert.Equal(TerminalEnrollmentStatus.Pending, slot.Status);
        Assert.Null(slot.DpopJkt);
    }

    [Fact]
    public async Task An_unbound_device_request_cannot_reach_an_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-unbound", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Ohne Schlüssel", ct);

        // Front door: the terminal client is RequireDpop — a proof-less device
        // request never even mints a code.
        var client = Factory.CreateClient();
        var proofless = await client.PostAsync("/connect/device",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("client_id", terminal.ClientId)]), ct);
        Assert.False(proofless.IsSuccessStatusCode);
        Assert.Contains("requires a DPoP proof", await proofless.Content.ReadAsStringAsync(ct));

        // Defense in depth (§11.4 check 7): if the binding row is gone by
        // approval time (expired + pruned), the consent shows no fingerprint
        // and the approval is refused.
        using var deviceKey = new DpopProofBuilder();
        var (_, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.DeleteWhere<Modgud.Domain.OAuth.Storage.DeviceCodeDpopBinding>(b => b.UserCodeHash != null);
            await session.SaveChangesAsync(ct);
        }

        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        var ticket = await OpenVerificationAsync(admin, userCode);
        var info = await GetVerificationInfoAsync(admin, ticket, ct);
        Assert.Equal("terminal", info.GetProperty("Kind").GetString());
        var hasFingerprint = info.GetProperty("Terminal").TryGetProperty("DpopFingerprint", out var fp) &&
                             fp.ValueKind is not JsonValueKind.Null;
        Assert.False(hasFingerprint);

        var approve = await SubmitDecisionAsync(admin, userCode, approve: true);
        Assert.True((int)approve.StatusCode >= 400 ||
            (await approve.Content.ReadAsStringAsync(ct)).Contains("DPoP"));

        var slot = await LoadSlotAsync(terminal.Id, ct);
        Assert.Equal(TerminalEnrollmentStatus.Pending, slot.Status);
        Assert.Null(slot.DpopJkt);
    }

    [Fact]
    public async Task A_wrong_key_poll_does_not_enroll_and_the_right_key_still_can()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-wrongkey", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Angriff", ct);

        using var deviceKey = new DpopProofBuilder();
        using var attackerKey = new DpopProofBuilder();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));

        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(admin, userCode);
        var approve = await SubmitDecisionAsync(admin, userCode, approve: true);
        Assert.True((int)approve.StatusCode < 400,
            $"approve failed ({(int)approve.StatusCode}): {await approve.Content.ReadAsStringAsync(ct)}");

        // Attacker polls the APPROVED code with their own key: refused BEFORE
        // anything is pinned — the regression this test exists for is a slot
        // enrolled with the attacker's key while the response still errors.
        var attack = await PollTokenAsync(terminal.ClientId, deviceCode,
            attackerKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Assert.False(attack.IsSuccessStatusCode);
        Assert.Contains("does not match the key", await attack.Content.ReadAsStringAsync(ct));

        var slot = await LoadSlotAsync(terminal.Id, ct);
        Assert.Equal(TerminalEnrollmentStatus.Pending, slot.Status);
        Assert.Null(slot.DpopJkt);

        // The refusal did not consume the code — the legitimate device enrolls.
        var legit = await PollTokenAsync(terminal.ClientId, deviceCode,
            deviceKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Assert.True(legit.IsSuccessStatusCode, await legit.Content.ReadAsStringAsync(ct));
        slot = await LoadSlotAsync(terminal.Id, ct);
        Assert.Equal(TerminalEnrollmentStatus.Active, slot.Status);
        Assert.Equal(deviceKey.Jkt, slot.DpopJkt);
    }

    [Fact]
    public async Task An_enrolled_slot_cannot_be_enrolled_again()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-reenroll", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Einmalig", ct);

        using var firstKey = new DpopProofBuilder();
        await EnrollAsync(terminal.ClientId, firstKey, ct);

        // A second device (fresh key) tries the same slot: the approval is
        // refused — key rotation is a fresh slot, never a silent re-enroll.
        using var secondKey = new DpopProofBuilder();
        var (_, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, secondKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(admin, userCode);
        var approve = await SubmitDecisionAsync(admin, userCode, approve: true);
        Assert.True((int)approve.StatusCode >= 400 ||
            (await approve.Content.ReadAsStringAsync(ct)).Contains("not pending"));

        var slot = await LoadSlotAsync(terminal.Id, ct);
        Assert.Equal(TerminalEnrollmentStatus.Active, slot.Status);
        Assert.Equal(firstKey.Jkt, slot.DpopJkt);
    }

    [Fact]
    public async Task Terminal_verification_is_dark_while_the_flag_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-dark", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Dunkel", ct);

        using var deviceKey = new DpopProofBuilder();
        var (_, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));

        SetFeatureFlag(false);
        try
        {
            var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
            var ticket = await OpenVerificationAsync(admin, userCode);

            // The code must read as INVALID — not as a person consent for a
            // terminal client.
            var info = await GetVerificationInfoAsync(admin, ticket, ct);
            Assert.Equal("invalid_code", info.GetProperty("Status").GetString());

            // And a direct approval attempt is refused, not user-dispatched.
            var approve = await SubmitDecisionAsync(admin, userCode, approve: true);
            Assert.True((int)approve.StatusCode >= 400 ||
                (await approve.Content.ReadAsStringAsync(ct)).Contains("not enabled"));

            var slot = await LoadSlotAsync(terminal.Id, ct);
            Assert.Equal(TerminalEnrollmentStatus.Pending, slot.Status);
        }
        finally
        {
            SetFeatureFlag(true);
        }
    }

    // ─── flow helpers ─────────────────────────────────────────────────────

    private async Task<string> CreatePositionAsync(string accountName, bool terminalEnabled, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = terminalEnabled ? new { Enabled = true } : null,
        }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        return (await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id;
    }

    private async Task<TerminalDto> CreateTerminalAsync(string positionId, string displayName, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync($"/api/position/{positionId}/terminals",
            new { DisplayName = displayName, Location = "Tor 3", WebAuthnRpId = RpId }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"terminal create failed ({(int)resp.StatusCode}): {body}");
        return (await resp.Content.ReadFromJsonAsync<TerminalDto>(JsonOptions, ct))!;
    }

    /// <summary>Terminal clients have no scp permissions — the device request
    /// carries no scope; the granted scopes come from the enrollment principal.</summary>
    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodeAsync(string clientId, string? dpopProof)
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/device")
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("client_id", clientId),
            }),
        };
        if (dpopProof is not null) request.Headers.Add(DpopConstants.HeaderName, dpopProof);

        var resp = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/device failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("device_code").GetString()!,
            doc.RootElement.GetProperty("user_code").GetString()!);
    }

    private async Task<string> OpenVerificationAsync(HttpClient cookieClient, string userCode)
    {
        var resp = await cookieClient.GetAsync(
            $"/connect/verify?user_code={Uri.EscapeDataString(userCode)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.StartsWith("/device?ticket=", location);
        return location["/device?ticket=".Length..];
    }

    private async Task<JsonElement> GetVerificationInfoAsync(HttpClient cookieClient, string ticket, CancellationToken ct)
    {
        var resp = await cookieClient.GetAsync($"/connect/device-verification?ticket={ticket}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"device-verification failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("Status", out _), $"unexpected body shape: {body}");
        return doc.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SubmitDecisionAsync(HttpClient cookieClient, string userCode, bool approve)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("user_code", userCode),
            new("decision", approve ? "approve" : "deny"),
        };
        return await cookieClient.PostAsync("/connect/verify", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PollTokenAsync(string clientId, string deviceCode, string? dpopProof)
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("grant_type", DeviceCodeGrant),
                new("device_code", deviceCode),
                new("client_id", clientId),
            }),
        };
        if (dpopProof is not null) request.Headers.Add(DpopConstants.HeaderName, dpopProof);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> RefreshTokenAsync(string clientId, string refreshToken, string dpopProof)
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", clientId),
            }),
        };
        request.Headers.Add(DpopConstants.HeaderName, dpopProof);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Full happy-path enrollment for tests that need an Active slot.</summary>
    private async Task EnrollAsync(string clientId, DpopProofBuilder key, CancellationToken ct)
    {
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            clientId, key.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(admin, userCode);
        var approve = await SubmitDecisionAsync(admin, userCode, approve: true);
        Assert.True((int)approve.StatusCode < 400,
            $"approve failed ({(int)approve.StatusCode}): {await approve.Content.ReadAsStringAsync(ct)}");
        var poll = await PollTokenAsync(clientId, deviceCode,
            key.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Assert.True(poll.IsSuccessStatusCode, await poll.Content.ReadAsStringAsync(ct));
    }

    private async Task<TerminalEnrollment> LoadSlotAsync(string terminalId, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<TerminalEnrollment>(new ShortGuid(terminalId).Guid, ct))!;
    }

    // Minimal ES256 DPoP proof factory — same shape as the spike's builder.
    private sealed class DpopProofBuilder : IDisposable
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
