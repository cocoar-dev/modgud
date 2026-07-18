using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end verification of Pushed Authorization Requests (RFC 9126, #118):
/// the client POSTs its authorization request to <c>/connect/par</c>, receives a
/// one-time <c>request_uri</c>, and hands only <c>client_id</c> + that
/// <c>request_uri</c> to <c>/connect/authorize</c> — the request parameters
/// never traverse the front channel.
///
/// <para>PAR is <b>offered, not required</b>: a direct (non-PAR) authorize
/// request must keep working, and discovery must advertise the endpoint without
/// setting <c>require_pushed_authorization_requests</c>.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PushedAuthorizationRequestTests : IntegrationTestBase
{
    public PushedAuthorizationRequestTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Discovery_advertises_the_par_endpoint_and_does_not_require_it()
    {
        var client = Factory.CreateClient();
        var resp = await client.GetAsync("/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);

        Assert.True(
            doc.RootElement.TryGetProperty("pushed_authorization_request_endpoint", out var parEndpoint),
            $"discovery is missing pushed_authorization_request_endpoint.\n{body}");
        Assert.EndsWith("/connect/par", parEndpoint.GetString());

        // Offered, not mandated — if the flag is present at all it must be false.
        if (doc.RootElement.TryGetProperty("require_pushed_authorization_requests", out var required))
            Assert.False(required.GetBoolean(),
                "PAR must not be globally required — it would break direct browser/device flows.");
    }

    [Fact]
    public async Task Par_push_then_authorize_via_request_uri_issues_a_code_and_token()
    {
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-par-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateConfidentialClientAsync(clientId, clientSecret, redirectUri, ["openid"]);

        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Par", lastname: "User", acronym: "pu",
            email: "pu@test.com", password: "TestPass1234");

        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var state = Guid.NewGuid().ToString("N");

        // 1) Push the full authorization request to /connect/par (client auth via Basic).
        var backChannel = Factory.CreateClient();
        var parBody = await PushAsync(backChannel, clientId, clientSecret, new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        using var parJson = JsonDocument.Parse(parBody);
        var requestUri = parJson.RootElement.GetProperty("request_uri").GetString()!;
        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", requestUri);
        Assert.True(parJson.RootElement.GetProperty("expires_in").GetInt32() > 0,
            "PAR response must carry a positive expires_in.");

        // 2) Authorize with ONLY client_id + request_uri (authenticated cookie).
        var cookieClient = await CreateAuthenticatedClientAsync("pu", "TestPass1234");
        var authorizeUri = "/connect/authorize?" +
            $"client_id={Uri.EscapeDataString(clientId)}&request_uri={Uri.EscapeDataString(requestUri)}";
        var authResp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);

        Assert.True((int)authResp.StatusCode is 301 or 302 or 303 or 307 or 308,
            $"authorize via request_uri should redirect to the callback, got {(int)authResp.StatusCode}: " +
            $"{await authResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var location = authResp.Headers.Location
            ?? throw new Xunit.Sdk.XunitException("no Location header on the authorize redirect");
        var query = HttpUtility.ParseQueryString(location.Query);
        var code = query["code"];
        Assert.False(string.IsNullOrEmpty(code),
            $"no 'code' in the authorize redirect. Location: {location}");
        Assert.Equal(state, query["state"]);

        // 3) Exchange the code — proves the pushed request drove a real grant.
        var tokenResp = await backChannel.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }), TestContext.Current.CancellationToken);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.IsSuccessStatusCode,
            $"/connect/token failed ({(int)tokenResp.StatusCode}): {tokenBody}");
        using var tokenJson = JsonDocument.Parse(tokenBody);
        Assert.False(string.IsNullOrEmpty(tokenJson.RootElement.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Authorize_with_an_unknown_request_uri_is_rejected()
    {
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-par-bad-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateConfidentialClientAsync(clientId, clientSecret, redirectUri, ["openid"]);

        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Par", lastname: "Bad", acronym: "pb",
            email: "pb@test.com", password: "TestPass1234");

        var cookieClient = await CreateAuthenticatedClientAsync("pb", "TestPass1234");
        // A request_uri that was never issued by /connect/par must not resolve.
        var bogus = "urn:ietf:params:oauth:request_uri:" + Guid.NewGuid().ToString("N");
        var authorizeUri = "/connect/authorize?" +
            $"client_id={Uri.EscapeDataString(clientId)}&request_uri={Uri.EscapeDataString(bogus)}";
        var resp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);

        // Either a direct 400, or a redirect carrying an OAuth error — never a code.
        if (resp.Headers.Location is { } location)
        {
            var query = HttpUtility.ParseQueryString(location.Query);
            Assert.Null(query["code"]);
            Assert.False(string.IsNullOrEmpty(query["error"]),
                $"an unknown request_uri must produce an error, not a silent success. Location: {location}");
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    private async Task<string> PushAsync(
        HttpClient client, string clientId, string clientSecret, Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/connect/par")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")));
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"/connect/par should return 201 Created, got {(int)resp.StatusCode}: {body}");
        return body;
    }

    private async Task CreateConfidentialClientAsync(
        string clientId, string clientSecret, string redirectUri, List<string> scopes)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [redirectUri],
            PostLogoutRedirectUris = [],
            Scopes = scopes,
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GeneratePkceS256Challenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
