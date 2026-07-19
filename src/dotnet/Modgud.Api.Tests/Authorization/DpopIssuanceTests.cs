using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end verification of DPoP issuance (RFC 9449, #118): a client that
/// presents a valid proof at <c>/connect/token</c> gets an access token bound to
/// the proof key (<c>cnf.jkt</c>) and returned as <c>token_type=DPoP</c>. DPoP is
/// offered, not required — a request with no proof yields an ordinary bearer
/// token. Invalid and replayed proofs are rejected with <c>invalid_dpop_proof</c>.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DpopIssuanceTests : IntegrationTestBase
{
    // The absolute token-endpoint URL as the test host sees it — the proof's htu
    // must match what OpenIddict reconstructs from the request.
    private const string TokenEndpoint = "http://localhost/connect/token";

    public DpopIssuanceTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Discovery_advertises_the_dpop_signing_algorithms()
    {
        var client = Factory.CreateClient();
        var resp = await client.GetAsync("/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);

        Assert.True(
            doc.RootElement.TryGetProperty("dpop_signing_alg_values_supported", out var algs),
            $"discovery is missing dpop_signing_alg_values_supported.\n{body}");
        Assert.Equal(JsonValueKind.Array, algs.ValueKind);
        var values = algs.EnumerateArray().Select(e => e.GetString()).ToList();
        // The advertised set must include the common EC + RSA proof algorithms.
        Assert.Contains("ES256", values);
        Assert.Contains("RS256", values);
        Assert.Contains("PS256", values);
    }

    [Fact]
    public async Task A_valid_proof_binds_the_access_token_and_marks_it_dpop()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-ok");
        var (user, pass) = await NewUserAsync("do", "do@test.com");

        using var proofKey = new DpopProofBuilder();
        var proof = proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow);

        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof);

        Assert.True(tokenResp.IsSuccessStatusCode,
            $"/connect/token failed: {tokenJson.RootElement}");

        var payload = DecodeJwtPayload(tokenJson.RootElement.GetProperty("access_token").GetString()!);
        Assert.True(payload.TryGetProperty("cnf", out var cnf), $"access token has no cnf claim: {payload}");
        Assert.Equal(JsonValueKind.Object, cnf.ValueKind);
        Assert.Equal(proofKey.Jkt, cnf.GetProperty("jkt").GetString());

        Assert.Equal("DPoP", tokenJson.RootElement.GetProperty("token_type").GetString());
    }

    [Fact]
    public async Task A_request_without_a_proof_yields_an_unbound_bearer_token()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-none");
        var (user, pass) = await NewUserAsync("dn", "dn@test.com");

        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, dpopProof: null);

        Assert.True(tokenResp.IsSuccessStatusCode, $"/connect/token failed: {tokenJson.RootElement}");
        Assert.Equal("Bearer", tokenJson.RootElement.GetProperty("token_type").GetString());

        var payload = DecodeJwtPayload(tokenJson.RootElement.GetProperty("access_token").GetString()!);
        Assert.False(payload.TryGetProperty("cnf", out _),
            "an unbound token must not carry a cnf claim");
    }

    [Fact]
    public async Task An_invalid_proof_is_rejected()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-bad");
        var (user, pass) = await NewUserAsync("db", "db@test.com");

        using var proofKey = new DpopProofBuilder();
        // Wrong htu — bound to a different URL than the actual token endpoint.
        var proof = proofKey.CreateProof("POST", "https://evil.example/token", DateTimeOffset.UtcNow);

        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof);

        Assert.Equal(HttpStatusCode.BadRequest, tokenResp.StatusCode);
        Assert.Equal("invalid_dpop_proof", tokenJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_replayed_proof_is_rejected()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-replay");
        var (user, pass) = await NewUserAsync("dr", "dr@test.com");

        using var proofKey = new DpopProofBuilder();
        // One proof (one jti), reused across two independent code exchanges.
        var proof = proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow);

        var (firstResp, firstJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof);
        Assert.True(firstResp.IsSuccessStatusCode, $"first exchange failed: {firstJson.RootElement}");
        Assert.Equal("DPoP", firstJson.RootElement.GetProperty("token_type").GetString());

        var (secondResp, secondJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof);
        Assert.Equal(HttpStatusCode.BadRequest, secondResp.StatusCode);
        Assert.Equal("invalid_dpop_proof", secondJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_client_that_requires_dpop_rejects_a_tokenless_request()
    {
        // RequireDpop flips the offered-not-required default for this client: a
        // token exchange with no DPoP header is rejected instead of downgraded to
        // an ordinary bearer token (RFC 9449 §5, #118).
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-req", requireDpop: true);
        var (user, pass) = await NewUserAsync("dq", "dq@test.com");

        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, dpopProof: null);

        Assert.Equal(HttpStatusCode.BadRequest, tokenResp.StatusCode);
        Assert.Equal("invalid_dpop_proof", tokenJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_client_that_requires_dpop_still_accepts_a_valid_proof()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-req-ok", requireDpop: true);
        var (user, pass) = await NewUserAsync("dw", "dw@test.com");

        using var proofKey = new DpopProofBuilder();
        var proof = proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow);

        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof);

        Assert.True(tokenResp.IsSuccessStatusCode, $"/connect/token failed: {tokenJson.RootElement}");
        Assert.Equal("DPoP", tokenJson.RootElement.GetProperty("token_type").GetString());

        var payload = DecodeJwtPayload(tokenJson.RootElement.GetProperty("access_token").GetString()!);
        Assert.True(payload.TryGetProperty("cnf", out var cnf), $"access token has no cnf claim: {payload}");
        Assert.Equal(proofKey.Jkt, cnf.GetProperty("jkt").GetString());
    }

    [Fact]
    public async Task A_nonce_required_client_challenges_a_proof_without_a_nonce()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-nonce", requireDpopNonce: true);
        var (user, pass) = await NewUserAsync("nc", "nc@test.com");

        using var proofKey = new DpopProofBuilder();
        var proof = proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow); // no nonce

        var (resp, json) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof, scope: "openid");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("use_dpop_nonce", json.RootElement.GetProperty("error").GetString());
        Assert.True(resp.Headers.TryGetValues("DPoP-Nonce", out var nonces), "response is missing the DPoP-Nonce header");
        Assert.False(string.IsNullOrEmpty(nonces!.FirstOrDefault()), "DPoP-Nonce header is empty");
    }

    [Fact]
    public async Task A_nonce_required_client_accepts_the_proof_after_the_nonce_handshake()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-nonce-ok", requireDpopNonce: true);
        var (user, pass) = await NewUserAsync("nk", "nk@test.com");

        using var proofKey = new DpopProofBuilder();

        // One authorization code, exchanged twice — the use_dpop_nonce rejection
        // must NOT consume the code, so the client can retry with the nonce.
        var (code, verifier) = await AuthorizeToCodeAsync(clientId, redirectUri, user, pass, "openid");

        var (firstResp, firstJson) = await TokenWithCodeAsync(
            clientId, secret, redirectUri, code, verifier,
            proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.BadRequest, firstResp.StatusCode);
        Assert.Equal("use_dpop_nonce", firstJson.RootElement.GetProperty("error").GetString());
        var nonce = firstResp.Headers.GetValues("DPoP-Nonce").First();

        // Retry the SAME code with a fresh proof carrying the issued nonce.
        var (secondResp, secondJson) = await TokenWithCodeAsync(
            clientId, secret, redirectUri, code, verifier,
            proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow, nonce: nonce));

        Assert.True(secondResp.IsSuccessStatusCode, $"nonce retry failed: {secondJson.RootElement}");
        Assert.Equal("DPoP", secondJson.RootElement.GetProperty("token_type").GetString());
    }

    [Fact]
    public async Task A_nonce_required_client_rejects_an_unrecognised_nonce()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-nonce-bad", requireDpopNonce: true);
        var (user, pass) = await NewUserAsync("nb", "nb@test.com");

        using var proofKey = new DpopProofBuilder();
        // A nonce the server never issued must not satisfy the requirement.
        var proof = proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow, nonce: "not-a-real-nonce");

        var (resp, json) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof, scope: "openid");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("use_dpop_nonce", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_dpop_bound_refresh_token_is_redeemable_with_the_same_key()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-rt-ok", offlineAccess: true);
        var (user, pass) = await NewUserAsync("ro", "ro@test.com");

        using var proofKey = new DpopProofBuilder();

        // Initial DPoP exchange with offline_access → bound access + refresh token.
        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass,
            proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow),
            scope: "openid offline_access");
        Assert.True(tokenResp.IsSuccessStatusCode, $"initial /connect/token failed: {tokenJson.RootElement}");
        var refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrEmpty(refreshToken), "offline_access should yield a refresh token");

        // Refresh with a FRESH proof from the SAME key → accepted, still bound.
        var (refreshResp, refreshJson) = await RefreshAsync(
            clientId, secret, refreshToken!,
            proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));

        Assert.True(refreshResp.IsSuccessStatusCode, $"refresh failed: {refreshJson.RootElement}");
        Assert.Equal("DPoP", refreshJson.RootElement.GetProperty("token_type").GetString());
        var payload = DecodeJwtPayload(refreshJson.RootElement.GetProperty("access_token").GetString()!);
        Assert.True(payload.TryGetProperty("cnf", out var cnf), $"refreshed token has no cnf: {payload}");
        Assert.Equal(proofKey.Jkt, cnf.GetProperty("jkt").GetString());
    }

    [Fact]
    public async Task A_dpop_bound_refresh_token_is_rejected_when_redeemed_with_a_different_key()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-rt-key", offlineAccess: true);
        var (user, pass) = await NewUserAsync("rk", "rk@test.com");

        using var proofKey = new DpopProofBuilder();
        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass,
            proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow),
            scope: "openid offline_access");
        Assert.True(tokenResp.IsSuccessStatusCode, $"initial /connect/token failed: {tokenJson.RootElement}");
        var refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString()!;

        // A different key's proof is structurally valid but must not redeem a
        // token bound to another key (RFC 9449 §5).
        using var attackerKey = new DpopProofBuilder();
        var (refreshResp, refreshJson) = await RefreshAsync(
            clientId, secret, refreshToken,
            attackerKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.BadRequest, refreshResp.StatusCode);
        Assert.Equal("invalid_dpop_proof", refreshJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_dpop_bound_refresh_token_is_rejected_when_redeemed_without_a_proof()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-rt-none", offlineAccess: true);
        var (user, pass) = await NewUserAsync("rn", "rn@test.com");

        using var proofKey = new DpopProofBuilder();
        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass,
            proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow),
            scope: "openid offline_access");
        Assert.True(tokenResp.IsSuccessStatusCode, $"initial /connect/token failed: {tokenJson.RootElement}");
        var refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString()!;

        // A stolen bound refresh token replayed without the key must be rejected.
        var (refreshResp, refreshJson) = await RefreshAsync(clientId, secret, refreshToken, dpopProof: null);

        Assert.Equal(HttpStatusCode.BadRequest, refreshResp.StatusCode);
        Assert.Equal("invalid_dpop_proof", refreshJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unbound_refresh_token_is_redeemable_without_a_proof()
    {
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-rt-unbound", offlineAccess: true);
        var (user, pass) = await NewUserAsync("ru", "ru@test.com");

        // No proof at issuance → an ordinary (unbound) refresh token.
        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, dpopProof: null, scope: "openid offline_access");
        Assert.True(tokenResp.IsSuccessStatusCode, $"initial /connect/token failed: {tokenJson.RootElement}");
        Assert.Equal("Bearer", tokenJson.RootElement.GetProperty("token_type").GetString());
        var refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString()!;

        // An unbound refresh token keeps working as a plain bearer credential.
        var (refreshResp, refreshJson) = await RefreshAsync(clientId, secret, refreshToken, dpopProof: null);

        Assert.True(refreshResp.IsSuccessStatusCode, $"unbound refresh failed: {refreshJson.RootElement}");
        Assert.Equal("Bearer", refreshJson.RootElement.GetProperty("token_type").GetString());
    }

    /// <summary>Redeems a refresh token, optionally presenting a <c>DPoP</c> proof.</summary>
    private async Task<(HttpResponseMessage, JsonDocument)> RefreshAsync(
        string clientId, string clientSecret, string refreshToken, string? dpopProof)
    {
        var backChannel = Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
        };
        if (dpopProof is not null)
            req.Headers.TryAddWithoutValidation("DPoP", dpopProof);

        var resp = await backChannel.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (resp, JsonDocument.Parse(body));
    }

    [Fact]
    public async Task Introspection_of_a_dpop_bound_reference_token_echoes_cnf_jkt()
    {
        // The resource-server client library reads cnf.jkt out of the
        // introspection response to enforce the DPoP binding on opaque reference
        // tokens — this pins that the AS actually surfaces it.
        var (clientId, secret, redirectUri) = await NewClientAsync("dpop-ref", AccessTokenType.Reference);
        var (user, pass) = await NewUserAsync("df", "df@test.com");

        using var proofKey = new DpopProofBuilder();
        var proof = proofKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow);

        var (tokenResp, tokenJson) = await RunCodeFlowAsync(
            clientId, secret, redirectUri, user, pass, proof);
        Assert.True(tokenResp.IsSuccessStatusCode, $"/connect/token failed: {tokenJson.RootElement}");
        Assert.Equal("DPoP", tokenJson.RootElement.GetProperty("token_type").GetString());
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;

        // Introspect with the token's own presenter client (authorised caller).
        var introBody = await IntrospectAsync(clientId, secret, accessToken);
        using var introJson = JsonDocument.Parse(introBody);
        Assert.True(introJson.RootElement.GetProperty("active").GetBoolean(),
            $"reference token should introspect as active: {introBody}");
        Assert.True(introJson.RootElement.TryGetProperty("cnf", out var cnf),
            $"introspection response is missing cnf: {introBody}");
        Assert.Equal(proofKey.Jkt, cnf.GetProperty("jkt").GetString());
    }

    private async Task<string> IntrospectAsync(string clientId, string clientSecret, string token)
    {
        var client = Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token,
                ["token_type_hint"] = "access_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        return await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    // ── flow helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs authorize → code → token once and returns the token response. When
    /// <paramref name="dpopProof"/> is non-null it is sent as the <c>DPoP</c>
    /// header on the token exchange.
    /// </summary>
    private async Task<(HttpResponseMessage, JsonDocument)> RunCodeFlowAsync(
        string clientId, string clientSecret, string redirectUri,
        string username, string password, string? dpopProof, string scope = "openid")
    {
        var (code, verifier) = await AuthorizeToCodeAsync(clientId, redirectUri, username, password, scope);
        return await TokenWithCodeAsync(clientId, clientSecret, redirectUri, code, verifier, dpopProof);
    }

    /// <summary>Drives authorize → code and returns the code + its PKCE verifier so
    /// the same code can be exchanged (and re-exchanged, e.g. a nonce retry).</summary>
    private async Task<(string code, string verifier)> AuthorizeToCodeAsync(
        string clientId, string redirectUri, string username, string password, string scope)
    {
        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var state = Guid.NewGuid().ToString("N");

        var cookieClient = await CreateAuthenticatedClientAsync(username, password);
        var authorizeUri = "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={state}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
        });
        var authResp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);
        var code = HttpUtility.ParseQueryString(authResp.Headers.Location!.Query)["code"];
        Assert.False(string.IsNullOrEmpty(code), "authorize did not yield a code");
        return (code!, verifier);
    }

    /// <summary>Exchanges an authorization code, optionally presenting a <c>DPoP</c>
    /// proof. Returns the raw response so callers can inspect status + headers.</summary>
    private async Task<(HttpResponseMessage, JsonDocument)> TokenWithCodeAsync(
        string clientId, string clientSecret, string redirectUri, string code, string verifier, string? dpopProof)
    {
        var backChannel = Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }),
        };
        if (dpopProof is not null)
            req.Headers.TryAddWithoutValidation("DPoP", dpopProof);

        var resp = await backChannel.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (resp, JsonDocument.Parse(body));
    }

    // ── setup helpers ───────────────────────────────────────────────────────

    private async Task<(string clientId, string secret, string redirectUri)> NewClientAsync(
        string prefix, AccessTokenType tokenType = AccessTokenType.Jwt, bool requireDpop = false,
        bool offlineAccess = false, bool requireDpopNonce = false)
    {
        var clientId = $"test-{prefix}-" + Guid.NewGuid().ToString("N");
        var secret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";

        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [redirectUri],
            PostLogoutRedirectUris = [],
            // offline_access is what makes OpenIddict mint a refresh token.
            Scopes = offlineAccess ? ["openid", "offline_access"] : ["openid"],
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RequireConsent = false,
            // JWT clients let the test decode the access token; Reference clients
            // exercise the introspection path.
            AccessTokenType = tokenType,
            // #118 — per-client "DPoP required" enforcement.
            RequireDpop = requireDpop,
            // #118 — per-client "DPoP server-nonce required" enforcement.
            RequireDpopNonce = requireDpopNonce,
        }, TestContext.Current.CancellationToken);

        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
        return (clientId, secret, redirectUri);
    }

    private async Task<(string username, string password)> NewUserAsync(string acronym, string email)
    {
        const string password = "TestPass1234";
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Dpop", lastname: acronym.ToUpperInvariant(), acronym: acronym,
            email: email, password: password);
        return (acronym, password);
    }

    // ── crypto helpers ──────────────────────────────────────────────────────

    /// <summary>Mints ES256 (P-256) DPoP proofs for one ephemeral key.</summary>
    private sealed class DpopProofBuilder : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Jkt { get; }

        public DpopProofBuilder()
        {
            var p = _ec.ExportParameters(false);
            Jkt = JwkThumbprint.ForEc("P-256", p.Q.X!, p.Q.Y!);
        }

        public string CreateProof(
            string htm, string htu, DateTimeOffset iat, string? jti = null, string? nonce = null)
        {
            var p = _ec.ExportParameters(false);
            var jwk = new { kty = "EC", crv = "P-256", x = B64(p.Q.X!), y = B64(p.Q.Y!) };
            var header = new { typ = "dpop+jwt", alg = "ES256", jwk };
            var payload = new Dictionary<string, object>
            {
                ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
                ["htm"] = htm,
                ["htu"] = htu,
                ["iat"] = iat.ToUnixTimeSeconds(),
            };
            if (nonce is not null) payload["nonce"] = nonce;
            var signingInput = $"{Seg(header)}.{Seg(payload)}";
            var sig = _ec.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{signingInput}.{B64(sig)}";
        }

        private static string Seg(object o) => B64(JsonSerializer.SerializeToUtf8Bytes(o, JsonOptions));
        private static string B64(byte[] b) => Base64Url.EncodeToString(b);

        public void Dispose() => _ec.Dispose();
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        return doc.RootElement.Clone();
    }

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    private static string GeneratePkceS256Challenge(string verifier)
        => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
