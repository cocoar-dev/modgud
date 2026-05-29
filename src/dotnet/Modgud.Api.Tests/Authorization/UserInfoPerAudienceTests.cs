using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Common;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end verification of the <c>/connect/userinfo</c> per-Audience
/// emission per the permission model §5: for every <c>aud</c> on the
/// token, a <c>resource_access[<aud>]</c> block is rendered whose
/// contents are gated by the scopes the client opted into:
/// <list type="bullet">
///   <item><c>scope=roles</c> → <c>roles</c> array</item>
///   <item><c>scope=permissions</c> → <c>permissions</c> array
///   (bypass-expanded + filtered to the per-RS subset)</item>
/// </list>
/// These tests request both scopes and assert both arrays;
/// per-scope-gating-only behaviour is exercised by a separate test
/// suite. App and Group are pure IdP-internal — they never appear
/// in the block.
///
/// <para>Drives the full auth-code+PKCE flow with an RFC-8707 <c>resource=</c>
/// indicator so the assertion is meaningful end-to-end.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class UserInfoPerAudienceTests : IntegrationTestBase
{
    public UserInfoPerAudienceTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task UserInfo_SingleAud_DirectGrant_Emits_Concrete_Permission()
    {
        // ── Arrange ──────────────────────────────────────────────────────
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write"), ("policy", "admin")]);
        const string alphaAudience = "https://alpha-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);

        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-spa-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id], scopes: ["openid", "roles", "permissions", alphaScopeName]);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Direct", lastname: "Grant", acronym: "dg",
            email: "dg@test.com", password: "TestPass1234");

        await GrantAsync(testUser.Id, roleAppSlug: "app-alpha", resourceType: "policy",
            actions: ["write"], groupBoundTo: ["app-alpha"]);

        // ── Act ──────────────────────────────────────────────────────────
        var alphaBlock = await DriveFlowAndReadAlphaBlockAsync(
            "dg", clientId, clientSecret, redirectUri, alphaScopeName, alphaAudience);

        // ── Assert ───────────────────────────────────────────────────────
        var permissions = ReadStringArray(alphaBlock, "permissions");
        Assert.Contains("policy:write", permissions);
        Assert.DoesNotContain("policy:read", permissions);
        Assert.DoesNotContain("policy:admin", permissions);

        // Roles: non-empty array. The role we created has a stable Id but
        // its Name is generated; we just check that it's present (the role
        // assignment IS the only path to having a grant).
        Assert.True(alphaBlock.TryGetProperty("roles", out var roles));
        Assert.NotEmpty(roles.EnumerateArray());
        // App and Group are IdP-internal — they MUST NOT leak into the block.
        Assert.False(alphaBlock.TryGetProperty("groups", out _),
            "groups must not appear in the public RS-block.");
        Assert.False(alphaBlock.TryGetProperty("app", out _),
            "app must not appear in the public RS-block.");
    }

    [Fact]
    public async Task UserInfo_ReferenceToken_Client_Emits_Concrete_Permission()
    {
        // Same assertion as the JWT-client case above, but the client is
        // configured for OPAQUE REFERENCE access tokens (the federation-v1
        // default — decision I14). The reference token's stored payload is a
        // realm-signed JWT; /connect/userinfo must resolve the reference,
        // load the payload, and validate its signature against the REALM key.
        // Regression guard for the RealmTokenValidationHandler bug where the
        // IsReferenceToken early-return left the global key pool in place →
        // 401 invalid_token (ID2090, "signing key not found").
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write"), ("policy", "admin")]);
        const string alphaAudience = "https://alpha-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);

        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-ref-spa-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id], scopes: ["openid", "roles", "permissions", alphaScopeName],
            accessTokenType: AccessTokenType.Reference);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Reference", lastname: "Token", acronym: "rt",
            email: "rt@test.com", password: "TestPass1234");

        await GrantAsync(testUser.Id, roleAppSlug: "app-alpha", resourceType: "policy",
            actions: ["write"], groupBoundTo: ["app-alpha"]);

        var alphaBlock = await DriveFlowAndReadAlphaBlockAsync(
            "rt", clientId, clientSecret, redirectUri, alphaScopeName, alphaAudience);

        var permissions = ReadStringArray(alphaBlock, "permissions");
        Assert.Contains("policy:write", permissions);
        Assert.DoesNotContain("policy:read", permissions);
        Assert.DoesNotContain("policy:admin", permissions);
    }

    [Fact]
    public async Task ReferenceToken_RefreshRedemption_Then_UserInfo_Succeeds()
    {
        // Reference REFRESH tokens are signed with the GLOBAL pool (not the
        // realm key — see RealmSigningKeyHandler), so the realm-key install in
        // RealmTokenValidationHandler must NOT break their redemption at
        // /connect/token. This drives a reference client through
        // authorize → token (with offline_access) → refresh-redeem → userinfo,
        // proving both the refresh path and the (newly fixed) reference-access
        // userinfo path work together.
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write")]);
        const string alphaAudience = "https://alpha-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);

        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-ref-refresh-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id],
            scopes: ["openid", "offline_access", "roles", "permissions", alphaScopeName],
            accessTokenType: AccessTokenType.Reference);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Refresh", lastname: "Reference", acronym: "rr",
            email: "rr@test.com", password: "TestPass1234");
        await GrantAsync(testUser.Id, roleAppSlug: "app-alpha", resourceType: "policy",
            actions: ["write"], groupBoundTo: ["app-alpha"]);

        // authorize → token (with offline_access so a refresh token is issued)
        using var tokens = await DriveAuthCodeFlowForTokensAsync(
            username: "rr", password: "TestPass1234",
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            scope: $"openid offline_access roles permissions {alphaScopeName}",
            resources: [alphaAudience]);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;

        // redeem the refresh token → fresh reference access token
        var newAccessToken = await RedeemRefreshTokenAsync(
            refreshToken, clientId, clientSecret, [alphaAudience]);

        // the fresh access token must still resolve at /connect/userinfo
        var userinfoClient = Factory.CreateClient();
        userinfoClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newAccessToken);
        var response = await userinfoClient.GetAsync("/connect/userinfo",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            $"/connect/userinfo after refresh failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("resource_access", out var ra)
            && ra.TryGetProperty(alphaAudience, out _),
            $"resource_access['{alphaAudience}'] missing after refresh.\nBody:\n{body}");
    }

    [Fact]
    public async Task UserInfo_ResourceAdmin_Expands_To_All_Resource_Actions()
    {
        // policy:admin grants every action on the policy resource within
        // app-alpha. UserInfo emits all three (policy:read/write/admin)
        // even though the user only has the policy:admin grant.
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write"), ("policy", "admin"),
                          ("knowledge", "read")]);
        const string alphaAudience = "https://alpha-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);
        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-spa-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id], scopes: ["openid", "roles", "permissions", alphaScopeName]);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Resource", lastname: "Admin", acronym: "ra",
            email: "ra@test.com", password: "TestPass1234");

        await GrantAsync(testUser.Id, roleAppSlug: "app-alpha", resourceType: "policy",
            actions: ["admin"], groupBoundTo: ["app-alpha"]);

        var alphaBlock = await DriveFlowAndReadAlphaBlockAsync(
            "ra", clientId, clientSecret, redirectUri, alphaScopeName, alphaAudience);

        var permissions = ReadStringArray(alphaBlock, "permissions");
        // Pre-expansion: <r>:admin pulls in every <r>:<a> in the catalog.
        Assert.Contains("policy:read",  permissions);
        Assert.Contains("policy:write", permissions);
        Assert.Contains("policy:admin", permissions);
        // Other resources are NOT pulled in — admin scope is per-resource.
        Assert.DoesNotContain("knowledge:read", permissions);
    }

    [Fact]
    public async Task UserInfo_RealmAdmin_Expands_To_Entire_Catalog()
    {
        // realm:admin trumps everything — every Catalog-Eintrag every reachable
        // App lands in the block. The synthetic "realm:admin" marker itself
        // doesn't appear (consumers see only concrete strings).
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write"), ("knowledge", "read")]);
        const string alphaAudience = "https://alpha-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);
        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-spa-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id], scopes: ["openid", "roles", "permissions", alphaScopeName]);

        // isRealmAdmin: true attaches the user to a System Admin role + a
        // wildcard-bound group, so the BoundTo filter pickets up app-alpha.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Realm", lastname: "Admin", acronym: "rl",
            email: "rl@test.com", password: "TestPass1234",
            isRealmAdmin: true);

        var alphaBlock = await DriveFlowAndReadAlphaBlockAsync(
            "rl", clientId, clientSecret, redirectUri, alphaScopeName, alphaAudience);

        var permissions = ReadStringArray(alphaBlock, "permissions");
        Assert.Contains("policy:read",    permissions);
        Assert.Contains("policy:write",   permissions);
        Assert.Contains("knowledge:read", permissions);
        // realm:admin marker itself must NOT appear — the doc says
        // "Konsumenten machen stumpfes exact-match", so concrete strings only.
        Assert.DoesNotContain("realm:admin", permissions);
    }

    // ─── shared flow helper ──────────────────────────────────────────────

    private async Task<JsonElement> DriveFlowAndReadAlphaBlockAsync(
        string username, string clientId, string clientSecret, string redirectUri,
        string scopeName, string audience)
    {
        var accessToken = await DriveAuthCodeFlowAsync(
            username: username, password: "TestPass1234",
            clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri,
            scope: $"openid roles permissions {scopeName}",
            resources: [audience]);

        var userinfoClient = Factory.CreateClient();
        userinfoClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await userinfoClient.GetAsync("/connect/userinfo",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            $"/connect/userinfo failed ({(int)response.StatusCode}): {body}");

        var doc = JsonDocument.Parse(body);
        var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("resource_access", out var resourceAccess),
            $"resource_access missing.\nBody:\n{pretty}");
        Assert.True(resourceAccess.TryGetProperty(audience, out var block),
            $"resource_access['{audience}'] missing. keys: {string.Join(",", resourceAccess.EnumerateObject().Select(p => p.Name))}\nBody:\n{pretty}");

        // Clone so the caller can use it after `doc` is disposed.
        return block.Clone();
    }

    private static List<string> ReadStringArray(JsonElement obj, string property)
    {
        Assert.True(obj.TryGetProperty(property, out var arr),
            $"property '{property}' missing on block:\n{obj}");
        return arr.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    // ─── Authorization Code + PKCE flow helper ───────────────────────────

    private async Task<string> DriveAuthCodeFlowAsync(
        string username, string password,
        string clientId, string clientSecret,
        string redirectUri,
        string scope,
        IReadOnlyList<string> resources)
    {
        using var json = await DriveAuthCodeFlowForTokensAsync(
            username, password, clientId, clientSecret, redirectUri, scope, resources);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Same as <see cref="DriveAuthCodeFlowAsync"/> but returns the full
    /// token-endpoint JSON response (so callers can read the refresh_token too).</summary>
    private async Task<JsonDocument> DriveAuthCodeFlowForTokensAsync(
        string username, string password,
        string clientId, string clientSecret,
        string redirectUri,
        string scope,
        IReadOnlyList<string> resources)
    {
        // 1. Cookie-login first so /connect/authorize sees an authenticated principal.
        var cookieClient = await CreateAuthenticatedClientAsync(username, password);

        // 2. PKCE pair.
        var verifier = GeneratePkceVerifier();
        var challenge = GeneratePkceS256Challenge(verifier);
        var state = Guid.NewGuid().ToString("N");

        // 3. Authorize request — disable redirect-following so we capture the code from the Location header.
        var authorizeUri = "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={state}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
        }.Concat(resources.Select(r => $"resource={Uri.EscapeDataString(r)}")));

        var authResponse = await cookieClient.GetAsync(authorizeUri, TestContext.Current.CancellationToken);
        if ((int)authResponse.StatusCode is not (301 or 302 or 303 or 307 or 308))
        {
            var body = await authResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            throw new Xunit.Sdk.XunitException(
                $"Expected redirect from /connect/authorize, got {(int)authResponse.StatusCode}.\nBody:\n{body}");
        }

        var location = authResponse.Headers.Location
            ?? throw new Xunit.Sdk.XunitException("No Location header on authorize redirect");
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var code = query["code"]
            ?? throw new Xunit.Sdk.XunitException(
                $"No 'code' in authorize redirect. Location: {location}\nQuery: {string.Join("&", query.AllKeys.Select(k => $"{k}={query[k]}"))}");

        // 4. Token exchange.
        var tokenClient = Factory.CreateClient();
        var tokenForm = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        // Resource indicator must also be sent at the token endpoint per RFC 8707.
        var tokenContent = new List<KeyValuePair<string, string>>(
            tokenForm.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)));
        foreach (var r in resources)
            tokenContent.Add(new KeyValuePair<string, string>("resource", r));

        var tokenResponse = await tokenClient.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(tokenContent),
            TestContext.Current.CancellationToken);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(tokenResponse.IsSuccessStatusCode,
            $"/connect/token failed ({(int)tokenResponse.StatusCode}): {tokenBody}");

        return JsonDocument.Parse(tokenBody);
    }

    /// <summary>Redeems a refresh token at /connect/token and returns the new access token.</summary>
    private async Task<string> RedeemRefreshTokenAsync(
        string refreshToken, string clientId, string clientSecret, IReadOnlyList<string> resources)
    {
        var tokenClient = Factory.CreateClient();
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
            new("client_secret", clientSecret),
        };
        foreach (var r in resources)
            form.Add(new KeyValuePair<string, string>("resource", r));

        var resp = await tokenClient.PostAsync(
            "/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode,
            $"refresh_token redemption failed ({(int)resp.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

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

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // ─── Helpers ──────────────────────────────────────────────────────────

    private async Task<App> CreateAppAsync(
        string slug, string displayName,
        IReadOnlyList<(string Resource, string Action)> permissions)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        var perms = permissions
            .Select(p => new AppPermission(Guid.NewGuid(), p.Resource, p.Action, Description: null))
            .ToList();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id,
            Slug: slug,
            DisplayName: displayName,
            Description: null,
            Permissions: perms,
            IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var loaded = await session.LoadAsync<App>(id, TestContext.Current.CancellationToken);
        return loaded!;
    }

    private async Task<OAuthApiState> CreateOAuthApiAsync(string name, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var app = await session.LoadAsync<App>(appId, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"App {appId} not found — create it before the API.");
        var allCatalogPermissionIds = app.Permissions.Select(p => p.Id).ToList();

        var id = Guid.NewGuid();
        var (aggregate, created) = OAuthApiAggregate.Create(
            id, name, displayName: name, description: null, enabled: true,
            scopes: Array.Empty<string>());
        session.Events.StartStream<OAuthApiAggregate>(id, created);
        session.Events.Append(id, aggregate.SetAppId(appId));
        session.Events.Append(id, aggregate.SetPermissionIds(allCatalogPermissionIds));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var loaded = await session.LoadAsync<OAuthApiState>(id, TestContext.Current.CancellationToken);
        return loaded!;
    }

    private async Task CreateOAuthClientAsync(
        string clientId, string clientSecret, string redirectUri, List<Guid> appIds,
        List<string> scopes, AccessTokenType accessTokenType = AccessTokenType.Jwt)
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
            AccessTokenType = accessTokenType,
            AppIds = [.. appIds.Select(g => new BuildingBlocks.Helper.ShortGuid(g).ToString())],
        };

        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task CreateScopeAsync(string name, List<string> resources, Guid? appId)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var dto = new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            Resources = resources,
            AppId = appId is null ? null : new BuildingBlocks.Helper.ShortGuid(appId.Value).ToString(),
        };

        var result = await oauthAdmin.CreateScopeAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateScopeAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task GrantAsync(
        Guid userId, string roleAppSlug, string resourceType,
        IReadOnlyList<string> actions, IReadOnlyList<string> groupBoundTo)
    {
        var permissions = actions.Select(a => (resourceType, a)).ToList();
        var role = await Factory.CreateTestRoleAsync(
            name: $"R_{Guid.NewGuid():N}",
            permissions: permissions,
            appSlug: roleAppSlug);
        await Factory.CreateTestGroupAsync(
            name: $"G_{Guid.NewGuid():N}",
            memberIds: [userId],
            roleIds: [role.Id],
            boundTo: groupBoundTo.ToList());
    }
}
