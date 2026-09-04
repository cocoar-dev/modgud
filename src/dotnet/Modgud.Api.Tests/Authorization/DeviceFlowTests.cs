using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end OAuth 2.0 Device Authorization Grant (RFC 8628) against the
/// testcontainer: a client requests a device/user code at
/// <c>POST /connect/device</c>, a logged-in user approves it on the hosted
/// verification flow (<c>connect/verify</c> → <c>/device</c> →
/// <c>connect/verify</c> POST), and the device then exchanges the device code
/// for tokens at <c>/connect/token</c>. Proves the previously-missing hosted
/// verification step completes the flow.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DeviceFlowTests : IntegrationTestBase
{
    public DeviceFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string Scope = "openid offline_access";

    [Fact]
    public async Task Happy_path_device_then_user_approves_then_token_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedDeviceClientAsync();

        // 1. Device requests a user/device code (no browser).
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(clientId);
        Assert.False(string.IsNullOrEmpty(deviceCode));
        Assert.False(string.IsNullOrEmpty(userCode));

        // 2. The user opens verification_uri_complete (?user_code=…) in a browser.
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        var ticket = await OpenVerificationAsync(cookieClient, userCode);

        // 3. The /device page resolves the code → client + scopes.
        var info = await GetDeviceInfoAsync(cookieClient, ticket);
        Assert.Equal("ready", info.GetProperty("Status").GetString());

        // 4. The user approves.
        var approveResp = await SubmitDecisionAsync(cookieClient, userCode, approve: true);
        Assert.True((int)approveResp.StatusCode < 400,
            $"approve failed ({(int)approveResp.StatusCode}): {await approveResp.Content.ReadAsStringAsync(ct)}");

        // 5. The device polls the token endpoint → tokens.
        var tokenResp = await PollTokenAsync(clientId, deviceCode);
        var body = await tokenResp.Content.ReadAsStringAsync(ct);
        Assert.True(tokenResp.IsSuccessStatusCode, $"/connect/token failed ({(int)tokenResp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("access_token").GetString()));

        // The originally-requested scopes must survive the verification step:
        // offline_access → a refresh token; the granted scope echoed back.
        var scope = doc.RootElement.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
        Assert.Contains("offline_access", scope);
        Assert.False(string.IsNullOrEmpty(
            doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null),
            "offline_access should yield a refresh token.");

        // ADR 0009 — the approving browser session is the sid of the device's tokens,
        // and the client now holds a grant for it (back-channel logout targets it).
        var idToken = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(doc.RootElement.GetProperty("id_token").GetString()!);
        var sid = Guid.Parse(idToken.GetClaim("sid").Value);
        await using var query = GetTenantedSession();
        Assert.NotNull(await query.LoadAsync<Modgud.Authentication.Domain.UserSession>(sid, ct));
        Assert.NotNull(await query.LoadAsync<Modgud.Authentication.Sessions.SessionGrant>(
            Modgud.Authentication.Sessions.SessionGrant.IdFor(sid, clientId), ct));
    }

    [Fact]
    public async Task Deny_keeps_device_code_unauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedDeviceClientAsync();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(clientId);

        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(cookieClient, userCode);
        await SubmitDecisionAsync(cookieClient, userCode, approve: false);

        var tokenResp = await PollTokenAsync(clientId, deviceCode);
        Assert.Equal(HttpStatusCode.BadRequest, tokenResp.StatusCode);
        using var doc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(ct));
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Equal("access_denied", error);
    }

    [Fact]
    public async Task Unauthenticated_verification_redirects_to_login()
    {
        var clientId = await SeedDeviceClientAsync();
        var (_, userCode) = await RequestDeviceCodeAsync(clientId);

        var anon = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var resp = await anon.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(userCode)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Invalid_user_code_does_not_resolve()
    {
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        // No device code requested — open verify with no code → needs_code, then submit a bogus code.
        var ticket = await OpenVerificationAsync(cookieClient, userCode: null);
        var submitResp = await cookieClient.PostAsJsonAsync(
            "/connect/device-verification/code",
            new { Ticket = ticket, UserCode = "ZZZZ-ZZZZ" },
            TestContext.Current.CancellationToken);
        Assert.True(submitResp.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await submitResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("invalid_code", doc.RootElement.GetProperty("Status").GetString());
    }

    // ─── Flow helpers ────────────────────────────────────────────────────

    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodeAsync(string clientId)
    {
        var client = Factory.CreateClient();
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("scope", Scope),
        };
        var resp = await client.PostAsync("/connect/device", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/device failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("device_code").GetString()!,
            doc.RootElement.GetProperty("user_code").GetString()!);
    }

    /// <summary>GET connect/verify (cookie auth) → expect 302 to /device?ticket=…;
    /// returns the ticket id.</summary>
    private async Task<string> OpenVerificationAsync(HttpClient cookieClient, string? userCode)
    {
        var noRedirect = WithoutAutoRedirect(cookieClient);
        var url = userCode is null
            ? "/connect/verify"
            : $"/connect/verify?user_code={Uri.EscapeDataString(userCode)}";
        var resp = await noRedirect.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.StartsWith("/device?ticket=", location);
        return location["/device?ticket=".Length..];
    }

    private async Task<JsonElement> GetDeviceInfoAsync(HttpClient cookieClient, string ticket)
    {
        var resp = await cookieClient.GetAsync($"/connect/device-verification?ticket={ticket}", TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"device-verification GET failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SubmitDecisionAsync(HttpClient cookieClient, string userCode, bool approve)
    {
        var noRedirect = WithoutAutoRedirect(cookieClient);
        var form = new List<KeyValuePair<string, string>>
        {
            new("user_code", userCode),
            new("decision", approve ? "approve" : "deny"),
        };
        return await noRedirect.PostAsync("/connect/verify", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PollTokenAsync(string clientId, string deviceCode)
    {
        var client = Factory.CreateClient();
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", DeviceCodeGrant),
            new("device_code", deviceCode),
            new("client_id", clientId),
        };
        return await client.PostAsync("/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    // The shared authenticated client follows redirects by default; device
    // verify asserts on the 302 itself, so use a non-redirecting client that
    // shares the same cookie container is not trivial — instead just create a
    // fresh authenticated client with auto-redirect disabled per call site.
    private HttpClient WithoutAutoRedirect(HttpClient cookieClient) => cookieClient;

    // ─── Seed ────────────────────────────────────────────────────────────

    private async Task<string> SeedDeviceClientAsync()
    {
        var clientId = $"device-client-{Guid.NewGuid():N}";
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientType = OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = "Device Test App",
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "offline_access"],
            AllowedGrantTypes = [DeviceCodeGrant, "refresh_token"],
            RequireConsent = false,
            RequireClientSecret = false,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
        return clientId;
    }
}
