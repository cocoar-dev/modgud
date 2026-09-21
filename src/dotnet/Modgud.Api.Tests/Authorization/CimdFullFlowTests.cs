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

    /// <summary>
    /// RFC 8252 §7.3: a native client takes an ephemeral loopback port at request time, so
    /// the server MUST accept any port for a loopback redirect URI. Every local MCP client
    /// (Claude Code, Cursor, VS Code, the MCP Inspector) publishes exactly this document —
    /// port-less loopback redirect URIs, no <c>application_type</c> — and then calls back
    /// on whatever port it got. The synthesized CIMD client used to be hard-wired
    /// <c>web</c>, which made OpenIddict demand an exact match and refuse all of them
    /// (reported from the field as ID2043). The tolerance is the port and nothing else.
    /// </summary>
    [Fact]
    public async Task Loopback_redirect_with_an_ephemeral_port_is_accepted_for_a_cimd_client()
    {
        await SeedAsync();
        Factory.CimdDocuments[_clientIdUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = _clientIdUrl,
            ["client_name"] = "Claude Code",
            ["redirect_uris"] = new[] { "http://localhost/callback", "http://127.0.0.1/callback" },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = Scope,
        });

        // Any port, on either loopback host — and the port-less form itself.
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://localhost:40489/callback"));
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://127.0.0.1:51234/callback"));
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://localhost/callback"));

        // A different path or a non-loopback host is still refused: no consent redirect.
        Assert.DoesNotContain("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://localhost:40489/other") ?? string.Empty);
        Assert.DoesNotContain("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://evil.test:40489/callback") ?? string.Empty);

        // And the code exchange completes with the ported URI it was issued for.
        var (accessToken, _) = await DriveCimdAuthCodeFlowAsync(
            _clientIdUrl, Scope, AllowedAudience, redirectUri: "http://localhost:40489/callback");
        Assert.Contains(AllowedAudience, new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Audiences);
    }

    /// <summary>
    /// A static CIMD document has no <c>scope</c> — Claude Code's is one document
    /// for every MCP server in the world and cannot know one server's scopes; the
    /// client learns them from the server's protected-resource metadata at run
    /// time. Such a client must hold the realm's dynamic-client scope set (the
    /// opted-in scopes plus the open standard scopes), so the per-scope opt-in is
    /// reachable at all. It used to hold nothing, and OpenIddict refused every
    /// scope but <c>openid</c>/<c>offline_access</c> with ID2051 before the opt-in
    /// was ever consulted (reported from the field). A scope the admin did not
    /// opt in stays refused — with Modgud's own message, not the opaque one — and
    /// a document that does declare <c>scope</c> cannot name its way past that.
    /// </summary>
    [Fact]
    public async Task A_document_without_scope_holds_the_realms_dynamic_client_scopes()
    {
        await SeedAsync();
        const string notOptedIn = "cimd-not-opted-in-scope";
        await CreateScopeAsync(notOptedIn, allowDynamicClients: false);
        Factory.CimdDocuments[_clientIdUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = _clientIdUrl,
            ["client_name"] = "Claude Code",
            ["redirect_uris"] = new[] { "http://localhost/callback", "http://127.0.0.1/callback" },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
        });
        const string callback = "http://localhost:40489/callback";

        // The opted-in scope and the OIDC identity scopes reach consent.
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, $"openid {ScopeName}", callback));
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, "openid profile email offline_access", callback));

        // A scope without the opt-in is refused — by Modgud, naming the flag. Request
        // validation errors are rendered by OpenIddict as a page, not redirected.
        await AssertRefusedScopeAsync(_clientIdUrl, $"openid {notOptedIn}", callback, "AllowDynamicRegistrationClients");

        // The management selector is never a dynamic client's to ask for.
        await AssertRefusedScopeAsync(_clientIdUrl, "openid modgud.management", callback, "not available to dynamically registered clients");

        // And the whole flow completes on the default set.
        var (accessToken, _) = await DriveCimdAuthCodeFlowAsync(_clientIdUrl, $"openid {ScopeName}", AllowedAudience, redirectUri: callback);
        Assert.Contains(AllowedAudience, new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Audiences);

        // A document that declares scope gets the intersection, never more.
        var declaring = $"https://cimd-app.test/oauth/{Guid.NewGuid():N}/declaring.json";
        Factory.CimdDocuments[declaring] = BuildDocument(declaring, $"openid {ScopeName} {notOptedIn}");
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(declaring, $"openid {ScopeName}"));
        await AssertRefusedScopeAsync(declaring, $"openid {notOptedIn}", RedirectUri, "AllowDynamicRegistrationClients");
    }

    /// <summary>
    /// A CIMD document describes the client for every authorization server, so it
    /// may list grants Modgud does not offer. claude.ai's connector document lists
    /// <c>urn:ietf:params:oauth:grant-type:jwt-bearer</c> next to
    /// <c>authorization_code</c> and <c>refresh_token</c>; the whole document used
    /// to be rejected for it, and the authorize request failed as an unknown client
    /// (ID2052, reported from the field). The unoffered grant is dropped; the client
    /// holds exactly what Modgud offers and the flow completes, refresh included.
    /// </summary>
    [Fact]
    public async Task A_document_listing_an_unoffered_grant_still_resolves_like_claude_ai()
    {
        await SeedAsync();
        Factory.CimdDocuments[_clientIdUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = _clientIdUrl,
            ["client_name"] = "Claude",
            ["client_uri"] = "https://cimd-app.test",
            ["redirect_uris"] = new[] { RedirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:jwt-bearer" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
        });

        var (accessToken, refreshToken) = await DriveCimdAuthCodeFlowAsync(
            _clientIdUrl, $"openid offline_access {ScopeName}", AllowedAudience, redirectUri: RedirectUri);
        Assert.Contains(AllowedAudience, new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Audiences);

        Assert.False(string.IsNullOrEmpty(refreshToken), "refresh_token is offered and listed — it must be issued.");
        var refreshed = await RefreshAsync(refreshToken, _clientIdUrl, AllowedAudience);
        Assert.Contains(AllowedAudience, new JwtSecurityTokenHandler().ReadJwtToken(refreshed).Audiences);
    }

    /// <summary>
    /// VS Code's document names its loopback redirect WITH a port
    /// (<c>http://127.0.0.1:33418/</c>) and falls back to another one when that
    /// port is taken. RFC 8252 §7.3: the server MUST allow any port for a loopback
    /// redirect — the synthesized client carries the port-less twin so OpenIddict
    /// does. The document also lists the device_code grant, which is dropped.
    /// </summary>
    [Fact]
    public async Task A_document_with_a_ported_loopback_uri_accepts_any_port_like_vs_code()
    {
        await SeedAsync();
        Factory.CimdDocuments[_clientIdUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = _clientIdUrl,
            ["client_name"] = "Visual Studio Code",
            ["redirect_uris"] = new[] { "http://127.0.0.1:33418/", RedirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["application_type"] = "native",
        });

        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://127.0.0.1:33418/"));
        Assert.StartsWith("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://127.0.0.1:58123/"));
        Assert.DoesNotContain("/consent?ticket=", await DriveAuthorizeAsync(_clientIdUrl, Scope, "http://127.0.0.1:58123/other") ?? string.Empty);

        var (accessToken, _) = await DriveCimdAuthCodeFlowAsync(
            _clientIdUrl, Scope, AllowedAudience, redirectUri: "http://127.0.0.1:58123/");
        Assert.Contains(AllowedAudience, new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Audiences);
    }

    /// <summary>
    /// ChatGPT's connector document authenticates with <c>private_key_jwt</c> and
    /// publishes its keys at a <c>jwks_uri</c> — the one real CIMD client that is not
    /// public, and the draft allows exactly that (only shared secrets are forbidden).
    /// The synthesized client is confidential: the code exchange and every refresh
    /// need an assertion signed by a key from the published set. No assertion, or
    /// one signed by a foreign key under the same kid, is invalid_client. When the
    /// client rotates, the first assertion with the new kid refetches the set at
    /// once; further unknown kids within a minute do not hit the client's host.
    /// </summary>
    [Fact]
    public async Task A_private_key_jwt_document_authenticates_with_keys_from_its_jwks_uri_like_chatgpt()
    {
        await SeedAsync();
        var ct = TestContext.Current.CancellationToken;
        using var keys = new TestJwks("chatgpt-1");
        using var rogue = new TestJwks("chatgpt-1"); // same kid, different key
        var jwksUri = $"https://cimd-app.test/oauth/{Guid.NewGuid():N}/jwks.json";
        Factory.CimdDocuments[jwksUri] = keys.PublicJwks;
        Factory.CimdDocuments[_clientIdUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = _clientIdUrl,
            ["client_name"] = "ChatGPT",
            ["redirect_uris"] = new[] { RedirectUri },
            ["token_endpoint_auth_method"] = "private_key_jwt",
            ["token_endpoint_auth_methods_supported"] = new[] { "none", "private_key_jwt" },
            ["token_endpoint_auth_signing_alg"] = "RS256",
            ["jwks_uri"] = jwksUri,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
        });
        var issuer = await IssuerAsync();
        var scope = $"openid offline_access {ScopeName}";

        // Without an assertion the code exchange is refused: the client is confidential.
        var anonymous = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience]);
        Assert.Contains("invalid_client", await anonymous.Content.ReadAsStringAsync(ct));

        // A foreign key under the published kid is refused as well.
        var forged = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            clientAssertion: rogue.MintAssertion(_clientIdUrl, issuer));
        Assert.Contains("invalid_client", await forged.Content.ReadAsStringAsync(ct));

        // Signed with the published key, the flow completes, refresh included.
        var ok = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            clientAssertion: keys.MintAssertion(_clientIdUrl, issuer));
        var okBody = await ok.Content.ReadAsStringAsync(ct);
        Assert.True(ok.IsSuccessStatusCode, okBody);
        string refreshToken;
        using (var doc = JsonDocument.Parse(okBody))
        {
            Assert.Contains(AllowedAudience, new JwtSecurityTokenHandler().ReadJwtToken(doc.RootElement.GetProperty("access_token").GetString()).Audiences);
            refreshToken = doc.RootElement.GetProperty("refresh_token").GetString()!;
        }
        var unauthenticatedRefresh = await RefreshResponseAsync(refreshToken, _clientIdUrl, AllowedAudience, clientAssertion: null);
        Assert.Contains("invalid_client", await unauthenticatedRefresh.Content.ReadAsStringAsync(ct));
        var refreshed = await RefreshResponseAsync(refreshToken, _clientIdUrl, AllowedAudience, keys.MintAssertion(_clientIdUrl, issuer));
        Assert.True(refreshed.IsSuccessStatusCode, await refreshed.Content.ReadAsStringAsync(ct));
        var fetchesBeforeRotation = Factory.CimdFetchCounts[jwksUri];

        // The client rotates to a new key under a new kid: picked up on first use.
        using var rotatedKeys = new TestJwks("chatgpt-2");
        Factory.CimdDocuments[jwksUri] = rotatedKeys.PublicJwks;
        var afterRotation = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            clientAssertion: rotatedKeys.MintAssertion(_clientIdUrl, issuer));
        Assert.True(afterRotation.IsSuccessStatusCode, await afterRotation.Content.ReadAsStringAsync(ct));
        Assert.Equal(fetchesBeforeRotation + 1, Factory.CimdFetchCounts[jwksUri]);

        // Another unknown kid right after is refused without fetching again.
        using var madeUp = new TestJwks("chatgpt-made-up");
        var probe = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            clientAssertion: madeUp.MintAssertion(_clientIdUrl, issuer));
        Assert.Contains("invalid_client", await probe.Content.ReadAsStringAsync(ct));
        Assert.Equal(fetchesBeforeRotation + 1, Factory.CimdFetchCounts[jwksUri]);
    }

    private async Task AssertRefusedScopeAsync(string clientId, string scope, string redirectUri, string expectedDescriptionPart)
    {
        var resp = await DriveAuthorizeResponseAsync(clientId, scope, redirectUri);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {resp.Headers.Location}\n{body}");
        Assert.Contains("invalid_scope", body);
        Assert.Contains(expectedDescriptionPart, body);
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

    // Audit #26 (re-audit follow-up) — endpoint-level proof of the claim-first,
    // single-use consume: the FIRST consent POST marks the ticket used, so a second
    // POST with the same ticket is rejected with 409 rather than minting a duplicate
    // authorization. (The concurrent-race leg is covered at the store layer by
    // SecurityAuditWave5Tests.ConsentTicket_ConcurrentConsume_SecondWriterRejected.)
    [Fact]
    public async Task Consent_ticket_is_single_use_second_post_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync();
        RegisterCimdDocument();

        var (ticketId, _) = await DriveToConsentModelAsync(_clientIdUrl, Scope);

        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        var scopes = Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var first = await cookieClient.PostAsJsonAsync("/connect/consent",
            new { Ticket = ticketId, Approved = true, ApprovedScopes = scopes }, ct);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync(ct));

        var second = await cookieClient.PostAsJsonAsync("/connect/consent",
            new { Ticket = ticketId, Approved = true, ApprovedScopes = scopes }, ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
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

    private Task CreateAllowedScopeAsync() => CreateScopeAsync(ScopeName, allowDynamicClients: true);

    private async Task CreateScopeAsync(string name, bool allowDynamicClients)
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            Resources = new List<string> { AllowedAudience },
            AllowDynamicRegistrationClients = allowDynamicClients,
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
        string clientId, string scope, string resource, string? redirectUri = null)
    {
        var tokenResp = await DriveCimdFlowThroughToTokenAsync(
            clientId, scope, authorizeResource: resource, tokenResources: new[] { resource }, redirectUri: redirectUri);
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
        string clientId, string scope, string authorizeResource, IReadOnlyList<string> tokenResources,
        string? redirectUri = null, string? clientAssertion = null)
    {
        redirectUri ??= RedirectUri;
        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        var authorizeUri = BuildAuthorizeUri(clientId, scope, challenge, authorizeResource, redirectUri);
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
            new("redirect_uri", redirectUri),
            new("code_verifier", verifier),
        };
        foreach (var r in tokenResources) tokenForm.Add(new KeyValuePair<string, string>("resource", r));
        AddClientAssertion(tokenForm, clientAssertion);

        return await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(tokenForm), TestContext.Current.CancellationToken);
    }

    /// <summary>Drives authorize → consent GET and returns (ticket, parsed
    /// ConsentModel) so a test can inspect the consent payload.</summary>
    private static void AddClientAssertion(List<KeyValuePair<string, string>> form, string? assertion)
    {
        if (assertion is null) return;
        form.Add(new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
        form.Add(new("client_assertion", assertion));
    }

    /// <summary>A refresh request, raw — the private_key_jwt tests decide on the
    /// response code rather than asserting success.</summary>
    private async Task<HttpResponseMessage> RefreshResponseAsync(string refreshToken, string clientId, string resource, string? clientAssertion)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
            new("resource", resource),
        };
        AddClientAssertion(form, clientAssertion);
        return await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    /// <summary>The audience of a client assertion: the issuer identifier, nothing
    /// else (draft-ietf-oauth-rfc7523bis §4; OpenIddict 7 refuses the token endpoint).</summary>
    private async Task<string> IssuerAsync()
    {
        using var doc = JsonDocument.Parse(await Factory.CreateClient().GetStringAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("issuer").GetString()!;
    }

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
    private async Task<string?> DriveAuthorizeAsync(string clientId, string scope, string? redirectUri = null)
        => (await DriveAuthorizeResponseAsync(clientId, scope, redirectUri)).Headers.Location?.ToString();

    private async Task<HttpResponseMessage> DriveAuthorizeResponseAsync(string clientId, string scope, string? redirectUri = null)
    {
        var challenge = GeneratePkceS256Challenge(GeneratePkceVerifier());
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        return await cookieClient.GetAsync(
            BuildAuthorizeUri(clientId, scope, challenge, AllowedAudience, redirectUri ?? RedirectUri),
            TestContext.Current.CancellationToken);
    }

    private async Task<bool> DiscoveryHasCimdFlagAsync()
    {
        var metaClient = Factory.CreateClient();
        var resp = await metaClient.GetAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.TryGetProperty("client_id_metadata_document_supported", out var flag)
            && flag.ValueKind == JsonValueKind.True;
    }

    private static string BuildAuthorizeUri(string clientId, string scope, string challenge, string resource, string? redirectUri = null) =>
        "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri ?? RedirectUri)}",
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
