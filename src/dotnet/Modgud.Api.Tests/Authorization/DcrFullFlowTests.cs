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
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Full DCR end-to-end against the testcontainer: register an anonymous
/// public PKCE client at <c>POST /connect/register</c>, drive the
/// <c>/connect/authorize → /connect/consent → /connect/token</c> dance
/// as a logged-in user, and assert the issued access token is
/// audience-bound to the opted-in resource server.
///
/// <para>Complements <see cref="DcrRegistrationEndpointTests"/> (which
/// pins the registration endpoint in isolation) with the cross-cutting
/// flow that exercises the validator, consent-info handler, audience-
/// containment, and last-used tracker in one continuous request chain.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DcrFullFlowTests : IntegrationTestBase
{
    public DcrFullFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    // Use an HTTPS absolute URI — matches RFC 8707 §2 + DCR validator's
    // expectations for an OAuthApi.Name.
    private const string AllowedAudience = "https://dcr-allowed.test/";
    private const string DisallowedAudience = "https://dcr-not-allowed.test/";
    private const string ScopeName = "dcr-allowed-scope";
    private const string RedirectUri = "http://localhost/cb";

    [Fact]
    public async Task Happy_path_register_then_authorize_consent_token_yields_audience_bound_jwt()
    {
        await SeedAsync();
        var clientId = await RegisterDcrClientAsync(scope: $"openid {ScopeName}");

        var (accessToken, _) = await DriveDcrAuthCodeFlowAsync(
            clientId: clientId,
            scope: $"openid {ScopeName}",
            resource: AllowedAudience);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

        // RFC 8707: aud is narrowed to exactly the requested resource(s).
        Assert.Contains(AllowedAudience, jwt.Audiences);

        // DCR default access-token lifetime is 15 min (DcrSettings default,
        // EnableDcrAsync below doesn't override). The fix for bug #30
        // writes the OpenIddict-recognized "tkn_lft:act" settings key on
        // the application at registration time, so OpenIddict's pipeline
        // applies it natively. Compute lifetime from iat+exp claims (the
        // jwt.ValidFrom defaults to MinValue when nbf is absent, which
        // makes .ValidTo - .ValidFrom useless). Allow ±60s for wall-
        // clock skew between issuing and reading.
        var iat = long.Parse(jwt.Payload["iat"].ToString()!);
        var exp = long.Parse(jwt.Payload["exp"].ToString()!);
        var lifetimeMinutes = (exp - iat) / 60.0;
        Assert.InRange(lifetimeMinutes, 14, 16);
    }

    [Fact]
    public async Task Last_used_at_advances_after_first_token_issue()
    {
        await SeedAsync();
        var clientId = await RegisterDcrClientAsync(scope: $"openid {ScopeName}");

        // Snapshot the registered_at + last_used_at — they should be
        // equal immediately after registration.
        var beforeFlow = await LoadStoredAsync(clientId);
        Assert.Equal(beforeFlow.DcrRegisteredAt, beforeFlow.DcrLastUsedAt);

        await DriveDcrAuthCodeFlowAsync(clientId, $"openid {ScopeName}", AllowedAudience);

        var afterFlow = await LoadStoredAsync(clientId);
        Assert.NotNull(afterFlow.DcrLastUsedAt);
        Assert.True(afterFlow.DcrLastUsedAt > beforeFlow.DcrRegisteredAt,
            $"LastUsedAt {afterFlow.DcrLastUsedAt} must advance past RegisteredAt {beforeFlow.DcrRegisteredAt}");
    }

    [Fact]
    public async Task Token_request_without_resource_indicator_is_rejected_with_invalid_target()
    {
        await SeedAsync();
        var clientId = await RegisterDcrClientAsync(scope: $"openid {ScopeName}");

        // Drive authorize + consent first (those succeed), then call
        // /connect/token WITHOUT resource — the audience-containment
        // handler must reject.
        var tokenResp = await DriveDcrFlowThroughToTokenAsync(
            clientId, $"openid {ScopeName}",
            authorizeResource: AllowedAudience,
            tokenResources: Array.Empty<string>());

        await AssertInvalidTargetAsync(tokenResp);
    }

    [Fact]
    public async Task App_scoped_scope_with_AllowDcrClients_true_passes_authorize_for_DCR_client()
    {
        // The fix for manual-smoke bug #29: an app-scoped scope opted in
        // via AllowDynamicRegistrationClients=true is reachable by a DCR
        // client even though DCR clients have no AppIds of their own.
        await SeedAsync();
        var app = await CreateAppAsync($"dcr-app-{Guid.NewGuid():N}", "DCR Test App");
        var apiName = "https://dcr-appscoped-test.example/";
        await CreateAppScopedApiAsync(apiName, app.Id, allowDcr: true);
        var appScopedScope = $"dcr-appscoped-scope-{Guid.NewGuid():N}";
        await CreateAppScopedScopeAsync(appScopedScope, app.Id, apiName, allowDcrClients: true);

        var clientId = await RegisterDcrClientAsync(scope: $"openid {appScopedScope}");

        var (accessToken, _) = await DriveDcrAuthCodeFlowAsync(
            clientId: clientId,
            scope: $"openid {appScopedScope}",
            resource: apiName);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        Assert.Contains(apiName, jwt.Audiences);
    }

    [Fact]
    public async Task App_scoped_scope_without_AllowDcrClients_rejects_authorize_for_DCR_client()
    {
        // Negative twin: the same setup but AllowDynamicRegistrationClients=false
        // on the scope. /connect/authorize must short-circuit with
        // invalid_scope before the user ever lands on the consent screen.
        await SeedAsync();
        var app = await CreateAppAsync($"dcr-app-{Guid.NewGuid():N}", "DCR Test App (negative)");
        var apiName = "https://dcr-appscoped-negative.example/";
        await CreateAppScopedApiAsync(apiName, app.Id, allowDcr: true);
        var appScopedScope = $"dcr-appscoped-scope-{Guid.NewGuid():N}";
        await CreateAppScopedScopeAsync(appScopedScope, app.Id, apiName, allowDcrClients: false);

        var clientId = await RegisterDcrClientAsync(scope: $"openid {appScopedScope}");

        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        var authorizeUri = "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            $"scope=openid+{Uri.EscapeDataString(appScopedScope)}",
            "state=neg",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(apiName)}",
        });

        var resp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);
        // The endpoint redirects back to redirect_uri with error=invalid_scope.
        AssertRedirect(resp);
        var loc = resp.Headers.Location!.ToString();
        Assert.Contains("error=invalid_scope", loc);
        Assert.Contains("error_description=", loc);
    }

    [Fact]
    public async Task Token_request_with_unauthorized_resource_is_rejected_with_invalid_target()
    {
        await SeedAsync();
        var clientId = await RegisterDcrClientAsync(scope: $"openid {ScopeName}");

        // resource= points at an API that exists but does NOT have
        // AllowDynamicRegistration=true. Since the scope-grant of
        // dcr-allowed-scope only includes AllowedAudience, the inner
        // ResourceIndicatorHandler will reject FIRST (scope-grant
        // containment). That's fine — both checks produce invalid_target
        // and we accept either error path for the assertion.
        var tokenResp = await DriveDcrFlowThroughToTokenAsync(
            clientId, $"openid {ScopeName}",
            authorizeResource: AllowedAudience,
            tokenResources: new[] { DisallowedAudience });

        await AssertInvalidTargetAsync(tokenResp);
    }

    [Fact]
    public async Task Authorization_response_iss_matches_discovery_issuer_rfc9207()
    {
        // RFC 9207 / MCP: the authorization response (the redirect back to the
        // client) MUST carry an `iss` parameter, and a strict client compares it
        // (simple string comparison) against the issuer from discovery. Modgud
        // derives the real issuer per-realm from the request host but configures
        // OpenIddict with a placeholder Options.Issuer — so the authorize-response
        // iss must be overridden to the realm host the same way discovery + the
        // token iss claim are, or it leaks the placeholder and strict MCP clients
        // reject the redirect.
        await SeedAsync();
        var clientId = await RegisterDcrClientAsync(scope: $"openid {ScopeName}");

        var metaClient = Factory.CreateClient();
        var metaResp = await metaClient.GetAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        using var metaDoc = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var discoveryIssuer = metaDoc.RootElement.GetProperty("issuer").GetString()!;
        Assert.True(
            metaDoc.RootElement.TryGetProperty("authorization_response_iss_parameter_supported", out var issSupported)
            && issSupported.GetBoolean(),
            "Discovery advertises authorization_response_iss_parameter_supported=true, so the iss MUST be correct.");

        var codeRedirect = await DriveToFinalAuthorizeRedirectAsync(clientId, $"openid {ScopeName}", AllowedAudience);
        var iss = System.Web.HttpUtility.ParseQueryString(codeRedirect.Query)["iss"];

        Assert.False(string.IsNullOrEmpty(iss),
            $"RFC 9207: authorization response must carry iss. Redirect: {codeRedirect}");
        Assert.True(iss == discoveryIssuer,
            $"RFC 9207 mismatch — authorization-response iss '{iss}' != discovery issuer '{discoveryIssuer}'. "
            + $"A strict MCP client rejects on this. Final redirect: {codeRedirect}");
    }

    // ─── Seed: enable DCR + create API+scope with the per-row flags on ──

    private async Task SeedAsync()
    {
        await EnableDcrAsync();
        await CreateAllowedApiAsync();
        await CreateDisallowedApiAsync();
        await CreateAllowedScopeAsync();
    }

    private async Task EnableDcrAsync()
    {
        using var scope = NewSystemTenantScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        // Rate limits raised for tests — DcrRateLimiter is a singleton
        // and keeps state across the shared SharedPostgresFixture, so
        // production defaults (5/h, 100/d) would trip mid-suite once a
        // handful of tests have registered clients from localhost.
        await settingsService.PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto
            {
                Enabled = true,
                PerIpRateLimitPerHour = 10_000,
                PerRealmRateLimitPerDay = 10_000,
            },
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Creates an OAuthApi whose Properties dict carries the
    /// AllowDynamicRegistration=true flag — the resource-target half of
    /// the DCR triple-opt-in.</summary>
    private async Task CreateAllowedApiAsync()
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var createResult = await oauthAdmin.CreateApiAsync(new CreateOAuthApiDto
        {
            Name = AllowedAudience,
            DisplayName = "DCR-allowed test API",
            AllowDynamicRegistration = true,
        }, TestContext.Current.CancellationToken);

        Assert.False(createResult.IsError,
            $"CreateApiAsync(allowed) failed: {DescribeErrors(createResult.Errors)}");
    }

    private async Task CreateDisallowedApiAsync()
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var createResult = await oauthAdmin.CreateApiAsync(new CreateOAuthApiDto
        {
            Name = DisallowedAudience,
            DisplayName = "API that DCR clients can't target",
            AllowDynamicRegistration = false,
        }, TestContext.Current.CancellationToken);

        Assert.False(createResult.IsError,
            $"CreateApiAsync(disallowed) failed: {DescribeErrors(createResult.Errors)}");
    }

    /// <summary>Creates an OAuthScope whose Resources include the allowed
    /// API name (so principals granted this scope receive that audience)
    /// AND whose Properties dict carries AllowDynamicRegistrationClients=true.</summary>
    private async Task CreateAllowedScopeAsync()
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var createResult = await oauthAdmin.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = ScopeName,
            DisplayName = ScopeName,
            Resources = new List<string> { AllowedAudience },
            AllowDynamicRegistrationClients = true,
        }, TestContext.Current.CancellationToken);

        Assert.False(createResult.IsError,
            $"CreateScopeAsync failed: {DescribeErrors(createResult.Errors)}");
    }

    // ─── DCR registration ───────────────────────────────────────────────

    private async Task<string> RegisterDcrClientAsync(string scope)
    {
        var http = Factory.CreateClient();
        var body = JsonContent.Create(new
        {
            client_name = "FullFlow Test Client",
            redirect_uris = new[] { RedirectUri },
            grant_types = new[] { "authorization_code" },
            scope,
        });

        var resp = await http.PostAsync("/connect/register", body, TestContext.Current.CancellationToken);
        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"DCR registration failed ({(int)resp.StatusCode}): {bodyText}");
        using var doc = JsonDocument.Parse(bodyText);
        return doc.RootElement.GetProperty("client_id").GetString()!;
    }

    // ─── Auth-code + consent dance ───────────────────────────────────────

    private async Task<(string AccessToken, string RefreshToken)> DriveDcrAuthCodeFlowAsync(
        string clientId, string scope, string resource)
    {
        var tokenResp = await DriveDcrFlowThroughToTokenAsync(
            clientId, scope,
            authorizeResource: resource,
            tokenResources: new[] { resource });

        var bodyText = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.IsSuccessStatusCode,
            $"/connect/token failed ({(int)tokenResp.StatusCode}): {bodyText}");
        using var doc = JsonDocument.Parse(bodyText);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() ?? string.Empty
            : string.Empty;
        return (accessToken, refreshToken);
    }

    private async Task<HttpResponseMessage> DriveDcrFlowThroughToTokenAsync(
        string clientId, string scope, string authorizeResource,
        IReadOnlyList<string> tokenResources)
    {
        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var state = Guid.NewGuid().ToString("N");

        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        // ── Step A: /connect/authorize → expect 302 to /consent?ticket=… ──
        var authorizeUri = "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={state}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(authorizeResource)}",
        });
        var authorizeResp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);
        AssertRedirect(authorizeResp);
        var consentLocation = authorizeResp.Headers.Location!.ToString();
        Assert.StartsWith("/consent?ticket=", consentLocation);

        var ticketId = consentLocation["/consent?ticket=".Length..];

        // ── Step B: GET /connect/consent?ticket=… → 200 + ConsentModel ──
        var consentInfoResp = await cookieClient.GetAsync(
            $"/connect/consent?ticket={ticketId}",
            TestContext.Current.CancellationToken);
        var consentInfoBody = await consentInfoResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(consentInfoResp.IsSuccessStatusCode,
            $"GET /connect/consent failed ({(int)consentInfoResp.StatusCode}): {consentInfoBody}");
        using (var consentDoc = JsonDocument.Parse(consentInfoBody))
        {
            // The IsDynamicallyRegistered flag — proves the consent flow
            // is wired up to surface the DCR marker to the SPA.
            Assert.True(consentDoc.RootElement.GetProperty("IsDynamicallyRegistered").GetBoolean());
        }

        // ── Step C: POST /connect/consent with Approved=true ──
        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var decisionResp = await cookieClient.PostAsJsonAsync(
            "/connect/consent",
            new { Ticket = ticketId, Approved = true, ApprovedScopes = requestedScopes },
            TestContext.Current.CancellationToken);
        var decisionBody = await decisionResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(decisionResp.IsSuccessStatusCode,
            $"POST /connect/consent failed ({(int)decisionResp.StatusCode}): {decisionBody}");
        using var decisionDoc = JsonDocument.Parse(decisionBody);
        var followUpUrl = decisionDoc.RootElement.GetProperty("RedirectUrl").GetString()!;
        Assert.StartsWith("/connect/authorize", followUpUrl);

        // ── Step D: follow the authorize redirect → 302 redirect_uri?code=… ──
        var followUpResp = await cookieClient.GetAsync(followUpUrl, TestContext.Current.CancellationToken);
        AssertRedirect(followUpResp);
        var codeRedirect = followUpResp.Headers.Location!;
        var query = System.Web.HttpUtility.ParseQueryString(codeRedirect.Query);
        var code = query["code"]
            ?? throw new Xunit.Sdk.XunitException(
                $"No code in final authorize redirect: {codeRedirect}\nQuery: {codeRedirect.Query}");

        // ── Step E: POST /connect/token ──
        var tokenClient = Factory.CreateClient();
        var tokenForm = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("client_id", clientId),
            new("redirect_uri", RedirectUri),
            new("code_verifier", verifier),
        };
        foreach (var r in tokenResources)
            tokenForm.Add(new KeyValuePair<string, string>("resource", r));

        return await tokenClient.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(tokenForm),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Drives /connect/authorize → consent → the final authorize
    /// redirect and returns its Location (the redirect back to redirect_uri
    /// carrying code + state + iss). Same dance as
    /// <see cref="DriveDcrFlowThroughToTokenAsync"/> steps A–D, but stops at the
    /// redirect so the caller can inspect the response parameters (e.g. iss).</summary>
    private async Task<Uri> DriveToFinalAuthorizeRedirectAsync(string clientId, string scope, string authorizeResource)
    {
        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var state = Guid.NewGuid().ToString("N");
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        var authorizeUri = "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={state}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(authorizeResource)}",
        });
        var authorizeResp = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);
        AssertRedirect(authorizeResp);
        var consentLocation = authorizeResp.Headers.Location!.ToString();
        Assert.StartsWith("/consent?ticket=", consentLocation);
        var ticketId = consentLocation["/consent?ticket=".Length..];

        // Load consent info (mirrors the SPA), then approve.
        var consentInfoResp = await cookieClient.GetAsync(
            $"/connect/consent?ticket={ticketId}", TestContext.Current.CancellationToken);
        Assert.True(consentInfoResp.IsSuccessStatusCode);

        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var decisionResp = await cookieClient.PostAsJsonAsync(
            "/connect/consent",
            new { Ticket = ticketId, Approved = true, ApprovedScopes = requestedScopes },
            TestContext.Current.CancellationToken);
        var decisionBody = await decisionResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(decisionResp.IsSuccessStatusCode,
            $"POST /connect/consent failed ({(int)decisionResp.StatusCode}): {decisionBody}");
        using var decisionDoc = JsonDocument.Parse(decisionBody);
        var followUpUrl = decisionDoc.RootElement.GetProperty("RedirectUrl").GetString()!;

        var followUpResp = await cookieClient.GetAsync(followUpUrl, TestContext.Current.CancellationToken);
        AssertRedirect(followUpResp);
        return followUpResp.Headers.Location!;
    }

    private static async Task AssertInvalidTargetAsync(HttpResponseMessage tokenResp)
    {
        var body = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400, got {(int)tokenResp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Equal("invalid_target", error);
    }

    private async Task<OAuthClientDto> LoadStoredAsync(string clientId)
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var listing = await oauthAdmin.GetClientsAsync(
            new PaginationRequest { Page = 1, PageSize = 100 },
            TestContext.Current.CancellationToken);
        return listing.Items.Single(c => c.ClientId == clientId);
    }

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
            throw new Xunit.Sdk.XunitException(
                $"Expected redirect, got {(int)resp.StatusCode}.\nBody:\n{body}");
        }
        Assert.NotNull(resp.Headers.Location);
    }

    private static string DescribeErrors(IEnumerable<ErrorOr.Error> errors) =>
        string.Join(", ", errors.Select(e => $"{e.Code}: {e.Description}"));

    private async Task<App> CreateAppAsync(string slug, string displayName)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id,
            Slug: slug,
            DisplayName: displayName,
            Description: null,
            Permissions: new List<AppPermission>(),
            IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var loaded = await session.LoadAsync<App>(id, TestContext.Current.CancellationToken);
        return loaded!;
    }

    private async Task CreateAppScopedApiAsync(string name, Guid appId, bool allowDcr)
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateApiAsync(new CreateOAuthApiDto
        {
            Name = name,
            DisplayName = name,
            AppId = new BuildingBlocks.Helper.ShortGuid(appId).ToString(),
            AllowDynamicRegistration = allowDcr,
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }

    private async Task CreateAppScopedScopeAsync(string name, Guid appId, string resource, bool allowDcrClients)
    {
        using var scope = NewSystemTenantScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            Resources = new List<string> { resource },
            AppId = new BuildingBlocks.Helper.ShortGuid(appId).ToString(),
            AllowDynamicRegistrationClients = allowDcrClients,
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }

    // ─── PKCE helpers (identical to UserInfoPerAudienceTests') ───────────

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GeneratePkceS256Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
