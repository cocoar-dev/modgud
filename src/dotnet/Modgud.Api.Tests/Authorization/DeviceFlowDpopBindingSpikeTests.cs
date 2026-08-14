using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// MG-FT-00 spike 1 — DPoP-bound device flow (RFC 9449 applied to RFC 8628,
/// plan §11.7). The terminal-enrollment threat this pins: a device/user code
/// leaks (shoulder-surfed, logged, phished past the approving admin) and an
/// attacker polls the token endpoint with it. With the binding, the code is
/// useless without the requesting device's private DPoP key:
///
/// 1. A device-authorization request carrying a DPoP proof mints a device code
///    bound to the proof key; the binding survives the end-user approval.
/// 2. Polling with a DIFFERENT key is rejected (invalid_dpop_proof) WITHOUT
///    consuming the code — the legitimate device keeps polling and still gets
///    its tokens, bound to its own key (cnf.jkt).
/// 3. A device request WITHOUT a proof stays a plain RFC 8628 flow (opt-in).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DeviceFlowDpopBindingSpikeTests : IntegrationTestBase
{
    public DeviceFlowDpopBindingSpikeTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string Scope = "openid offline_access";
    private const string DeviceEndpoint = "http://localhost/connect/device";
    private const string TokenEndpoint = "http://localhost/connect/token";

    [Fact]
    public async Task Bound_device_code_rejects_second_key_and_still_redeems_for_the_requesting_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedDeviceClientAsync();

        using var requestingKey = new DpopProofBuilder();
        using var attackerKey = new DpopProofBuilder();

        // 1. Device request WITH a DPoP proof → device code bound to requestingKey.
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            clientId, requestingKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));

        // 2. The user approves on the hosted verification flow.
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(cookieClient, userCode);
        var approveResp = await SubmitDecisionAsync(cookieClient, userCode, approve: true);
        Assert.True((int)approveResp.StatusCode < 400,
            $"approve failed ({(int)approveResp.StatusCode}): {await approveResp.Content.ReadAsStringAsync(ct)}");

        // 3. An attacker polls the APPROVED code with their own key → rejected.
        var attackerResp = await PollTokenAsync(clientId, deviceCode,
            attackerKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Assert.False(attackerResp.IsSuccessStatusCode);
        var attackerBody = await attackerResp.Content.ReadAsStringAsync(ct);
        Assert.Contains(DpopConstants.InvalidProofError, attackerBody);

        // ...and polling with NO proof at all is rejected the same way.
        var proofless = await PollTokenAsync(clientId, deviceCode, dpopProof: null);
        Assert.False(proofless.IsSuccessStatusCode);
        Assert.Contains(DpopConstants.InvalidProofError, await proofless.Content.ReadAsStringAsync(ct));

        // 4. The rejections did NOT consume the code: the legitimate device's
        //    next poll succeeds and the access token is bound to ITS key.
        var legitResp = await PollTokenAsync(clientId, deviceCode,
            requestingKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        var legitBody = await legitResp.Content.ReadAsStringAsync(ct);
        Assert.True(legitResp.IsSuccessStatusCode, $"/connect/token failed ({(int)legitResp.StatusCode}): {legitBody}");

        using var doc = JsonDocument.Parse(legitBody);
        Assert.Equal("DPoP", doc.RootElement.GetProperty("token_type").GetString());
        var payload = DecodeJwtPayload(doc.RootElement.GetProperty("access_token").GetString()!);
        Assert.Equal(requestingKey.Jkt, payload.GetProperty("cnf").GetProperty("jkt").GetString());
    }

    [Fact]
    public async Task Device_request_without_proof_stays_a_plain_unbound_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedDeviceClientAsync();

        var (deviceCode, userCode) = await RequestDeviceCodeAsync(clientId, dpopProof: null);

        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(cookieClient, userCode);
        await SubmitDecisionAsync(cookieClient, userCode, approve: true);

        // No binding was established, so a proof-less poll succeeds (RFC 8628
        // unchanged) and yields an ordinary bearer token.
        var tokenResp = await PollTokenAsync(clientId, deviceCode, dpopProof: null);
        var body = await tokenResp.Content.ReadAsStringAsync(ct);
        Assert.True(tokenResp.IsSuccessStatusCode, $"/connect/token failed ({(int)tokenResp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Bearer", doc.RootElement.GetProperty("token_type").GetString());
    }

    // ─── Flow helpers (DeviceFlowTests', extended with an optional DPoP header) ──

    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodeAsync(string clientId, string? dpopProof = null)
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/device")
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("client_id", clientId),
                new("scope", Scope),
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

    private async Task<string> SeedDeviceClientAsync()
    {
        var clientId = $"dpop-device-{Guid.NewGuid():N}";
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientType = OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = "DPoP Device Spike App",
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "offline_access"],
            AllowedGrantTypes = [DeviceCodeGrant, "refresh_token"],
            RequireConsent = false,
            RequireClientSecret = false,
            // JWT so the test can decode the payload and assert cnf.jkt — the
            // binding itself is format-agnostic.
            AccessTokenType = AccessTokenType.Jwt,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
        return clientId;
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        return doc.RootElement.Clone();
    }

    // Minimal ES256 DPoP proof factory — a copy of DpopIssuanceTests' private
    // builder (kept private there; duplicated rather than refactored in a spike).
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
