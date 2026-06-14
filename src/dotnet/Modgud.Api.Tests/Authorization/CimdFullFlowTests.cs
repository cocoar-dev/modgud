using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.RealmSettings;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Full CIMD end-to-end against the testcontainer: a client whose
/// <c>client_id</c> is an https URL is resolved on demand from a stubbed
/// metadata document (no real network — see
/// <see cref="ModgudWebApplicationFactory.CimdDocuments"/>), then driven
/// through <c>/connect/authorize → /connect/consent → /connect/token</c> and a
/// refresh as a logged-in user. Proves the non-persisted synthesized client
/// survives the full auth-code + refresh flow (Option A) and is subject to the
/// same DCR audience containment.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class CimdFullFlowTests : IntegrationTestBase
{
    public CimdFullFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string AllowedAudience = "https://cimd-allowed.test/";
    private const string ScopeName = "cimd-allowed-scope";
    private const string ClientHost = "cimd-app.test";
    private const string RedirectUri = "https://cimd-app.test/callback";
    private const string Scope = "openid offline_access cimd-allowed-scope";

    // Per-test-instance unique client_id URL. The CIMD metadata cache is a
    // process-wide singleton that survives the per-test Marten reset, so a
    // shared URL would let one test serve another's cached document. Varying
    // the path (host stays constant for the hostname assertion) isolates the
    // cache key per test.
    private readonly string _clientIdUrl = $"https://cimd-app.test/oauth/{Guid.NewGuid():N}/client-metadata.json";

    [Fact]
    public async Task Happy_path_resolves_cimd_url_then_authorize_consent_token_yields_audience_bound_jwt()
    {
        await SeedAsync();
        RegisterCimdDocument();

        var (accessToken, refreshToken) = await DriveCimdAuthCodeFlowAsync(
            clientId: _clientIdUrl, scope: Scope, resource: AllowedAudience);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

        // RFC 8707: aud narrowed to exactly the requested resource.
        Assert.Contains(AllowedAudience, jwt.Audiences);

        // CIMD clients get JWT access tokens (a parseable JWT proves it — a
        // reference token wouldn't read as a JWT) on the CimdSettings default
        // 15-minute lifetime.
        var iat = long.Parse(jwt.Payload["iat"].ToString()!);
        var exp = long.Parse(jwt.Payload["exp"].ToString()!);
        var lifetimeMinutes = (exp - iat) / 60.0;
        Assert.InRange(lifetimeMinutes, 14, 16);

        // The non-persisted client survives a refresh — refresh re-resolves it
        // via FindByClientIdAsync (Option A), no DB record required.
        Assert.False(string.IsNullOrEmpty(refreshToken), "offline_access should yield a refresh token.");
        var refreshed = await RefreshAsync(refreshToken, _clientIdUrl, AllowedAudience);
        var refreshedJwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshed);
        Assert.Contains(AllowedAudience, refreshedJwt.Audiences);
    }

    [Fact]
    public async Task Consent_surfaces_hostname_and_unverified_marker()
    {
        await SeedAsync();
        RegisterCimdDocument();

        var (_, consentModel) = await DriveToConsentModelAsync(_clientIdUrl, Scope);

        Assert.True(consentModel.GetProperty("IsDynamicallyRegistered").GetBoolean(),
            "A CIMD client must show the unverified marker.");
        Assert.Equal(ClientHost, consentModel.GetProperty("ClientIdHostname").GetString());
    }

    [Fact]
    public async Task Discovery_advertises_cimd_support_only_when_enabled()
    {
        // Disabled (fresh realm, no enable): the flag must be absent.
        Assert.False(await DiscoveryHasCimdFlagAsync(),
            "client_id_metadata_document_supported must be absent when CIMD is off.");

        // Enabled: the flag must be present + true.
        await EnableCimdAsync();
        Assert.True(await DiscoveryHasCimdFlagAsync(),
            "client_id_metadata_document_supported must be true when CIMD is on.");
    }

    [Fact]
    public async Task Authorize_is_rejected_when_realm_cimd_disabled()
    {
        // Seed the API + scope but DO NOT enable CIMD. The client_id URL must
        // not resolve → authorize never reaches consent.
        await CreateAllowedApiAsync();
        await CreateAllowedScopeAsync();
        RegisterCimdDocument();

        var location = await DriveAuthorizeAsync(_clientIdUrl, Scope);
        Assert.DoesNotContain("/consent?ticket=", location ?? string.Empty);
    }

    [Fact]
    public async Task Authorize_is_rejected_when_document_client_id_mismatches()
    {
        await SeedAsync();
        // Register a document whose client_id does NOT match the URL — the
        // resolver's validator rejects it, so the client never resolves.
        Factory.CimdDocuments[_clientIdUrl] = BuildDocument(
            docClientId: "https://attacker.test/evil", scope: Scope);

        var location = await DriveAuthorizeAsync(_clientIdUrl, Scope);
        Assert.DoesNotContain("/consent?ticket=", location ?? string.Empty);
    }

    [Fact]
    public async Task Token_request_without_resource_indicator_is_rejected_with_invalid_target()
    {
        await SeedAsync();
        RegisterCimdDocument();

        var tokenResp = await DriveCimdFlowThroughToTokenAsync(
            _clientIdUrl, Scope, authorizeResource: AllowedAudience, tokenResources: Array.Empty<string>());

        var body = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)tokenResp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_target", doc.RootElement.GetProperty("error").GetString());
    }

    // ─── Seed ────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        await EnableCimdAsync();
        await CreateAllowedApiAsync();
        await CreateAllowedScopeAsync();
    }

    private async Task EnableCimdAsync()
    {
        using var scope = NewSystemTenantScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settingsService.PatchAsync(new UpdateRealmSettingsDto
        {
            Cimd = new UpdateCimdSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
    }

    private async Task CreateAllowedApiAsync()
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateApiAsync(new CreateOAuthApiDto
        {
            Name = AllowedAudience,
            DisplayName = "CIMD-allowed test API",
            AllowDynamicRegistration = true,
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }

    private async Task CreateAllowedScopeAsync()
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = ScopeName,
            DisplayName = ScopeName,
            Resources = new List<string> { AllowedAudience },
            AllowDynamicRegistrationClients = true,
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }

    // ─── CIMD document stub ──────────────────────────────────────────────

    private void RegisterCimdDocument() =>
        Factory.CimdDocuments[_clientIdUrl] = BuildDocument(_clientIdUrl, Scope);

    private static string BuildDocument(string docClientId, string scope) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = docClientId,
            ["client_name"] = "CIMD Test App",
            ["redirect_uris"] = new[] { RedirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = scope,
        });

    // ─── Flow drivers ────────────────────────────────────────────────────

    private async Task<(string AccessToken, string RefreshToken)> DriveCimdAuthCodeFlowAsync(
        string clientId, string scope, string resource)
    {
        var tokenResp = await DriveCimdFlowThroughToTokenAsync(
            clientId, scope, authorizeResource: resource, tokenResources: new[] { resource });
        var bodyText = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.IsSuccessStatusCode, $"/connect/token failed ({(int)tokenResp.StatusCode}): {bodyText}");
        using var doc = JsonDocument.Parse(bodyText);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        return (accessToken, refreshToken);
    }

    private async Task<string> RefreshAsync(string refreshToken, string clientId, string resource)
    {
        var tokenClient = Factory.CreateClient();
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
            new("resource", resource),
        };
        var resp = await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"refresh failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpResponseMessage> DriveCimdFlowThroughToTokenAsync(
        string clientId, string scope, string authorizeResource, IReadOnlyList<string> tokenResources)
    {
        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        var authorizeUri = BuildAuthorizeUri(clientId, scope, challenge, authorizeResource);
        var authorizeResp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);
        AssertRedirect(authorizeResp);
        var consentLocation = authorizeResp.Headers.Location!.ToString();
        Assert.StartsWith("/consent?ticket=", consentLocation);
        var ticketId = consentLocation["/consent?ticket=".Length..];

        var consentInfoResp = await cookieClient.GetAsync($"/connect/consent?ticket={ticketId}", TestContext.Current.CancellationToken);
        Assert.True(consentInfoResp.IsSuccessStatusCode);

        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var decisionResp = await cookieClient.PostAsJsonAsync(
            "/connect/consent",
            new { Ticket = ticketId, Approved = true, ApprovedScopes = requestedScopes },
            TestContext.Current.CancellationToken);
        var decisionBody = await decisionResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(decisionResp.IsSuccessStatusCode, $"POST /connect/consent failed ({(int)decisionResp.StatusCode}): {decisionBody}");
        using var decisionDoc = JsonDocument.Parse(decisionBody);
        var followUpUrl = decisionDoc.RootElement.GetProperty("RedirectUrl").GetString()!;

        var followUpResp = await cookieClient.GetAsync(followUpUrl, TestContext.Current.CancellationToken);
        AssertRedirect(followUpResp);
        var codeRedirect = followUpResp.Headers.Location!;
        var code = System.Web.HttpUtility.ParseQueryString(codeRedirect.Query)["code"]
            ?? throw new Xunit.Sdk.XunitException($"No code in final authorize redirect: {codeRedirect}");

        var tokenClient = Factory.CreateClient();
        var tokenForm = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("client_id", clientId),
            new("redirect_uri", RedirectUri),
            new("code_verifier", verifier),
        };
        foreach (var r in tokenResources) tokenForm.Add(new KeyValuePair<string, string>("resource", r));

        return await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(tokenForm), TestContext.Current.CancellationToken);
    }

    /// <summary>Drives authorize → consent GET and returns (ticket, parsed
    /// ConsentModel) so a test can inspect the consent payload.</summary>
    private async Task<(string Ticket, JsonElement Model)> DriveToConsentModelAsync(string clientId, string scope)
    {
        var challenge = GeneratePkceS256Challenge(GeneratePkceVerifier());
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        var authorizeResp = await cookieClient.GetAsync(BuildAuthorizeUri(clientId, scope, challenge, AllowedAudience), TestContext.Current.CancellationToken);
        AssertRedirect(authorizeResp);
        var consentLocation = authorizeResp.Headers.Location!.ToString();
        Assert.StartsWith("/consent?ticket=", consentLocation);
        var ticketId = consentLocation["/consent?ticket=".Length..];

        var consentInfoResp = await cookieClient.GetAsync($"/connect/consent?ticket={ticketId}", TestContext.Current.CancellationToken);
        var body = await consentInfoResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(consentInfoResp.IsSuccessStatusCode, $"GET /connect/consent failed ({(int)consentInfoResp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return (ticketId, doc.RootElement.Clone());
    }

    /// <summary>Drives just the authorize GET; returns the redirect Location
    /// (or null when the response isn't a redirect).</summary>
    private async Task<string?> DriveAuthorizeAsync(string clientId, string scope)
    {
        var challenge = GeneratePkceS256Challenge(GeneratePkceVerifier());
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        var resp = await cookieClient.GetAsync(BuildAuthorizeUri(clientId, scope, challenge, AllowedAudience), TestContext.Current.CancellationToken);
        return resp.Headers.Location?.ToString();
    }

    private async Task<bool> DiscoveryHasCimdFlagAsync()
    {
        var metaClient = Factory.CreateClient();
        var resp = await metaClient.GetAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.TryGetProperty("client_id_metadata_document_supported", out var flag)
            && flag.ValueKind == JsonValueKind.True;
    }

    private static string BuildAuthorizeUri(string clientId, string scope, string challenge, string resource) =>
        "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={Guid.NewGuid():N}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(resource)}",
        });

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }

    private static void AssertRedirect(HttpResponseMessage resp)
    {
        if ((int)resp.StatusCode is not (301 or 302 or 303 or 307 or 308))
        {
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new Xunit.Sdk.XunitException($"Expected redirect, got {(int)resp.StatusCode}.\nBody:\n{body}");
        }
        Assert.NotNull(resp.Headers.Location);
    }

    private static string DescribeErrors(IEnumerable<ErrorOr.Error> errors) =>
        string.Join(", ", errors.Select(e => $"{e.Code}: {e.Description}"));

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GeneratePkceS256Challenge(string verifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
