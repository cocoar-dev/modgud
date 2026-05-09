using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Domain.OAuth.Apis;
using Cocoar.Auth.Domain.OAuth.Common;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Api.Tests.Authorization;

/// <summary>
/// End-to-end verification that <c>/connect/userinfo</c> is a <b>pure identity
/// slice</b> per permission-modell §5: even when the token has audiences, the
/// user has app-scoped permissions, and the <c>roles</c> scope is requested,
/// UserInfo MUST NOT emit <c>resource_access</c>, <c>roles</c>, <c>permissions</c>
/// or <c>groups</c>. Authz info lives behind the distribution API (consumed by
/// the Cocoar.Auth.Client.AspNetCore helper lib), which UserInfo can't substitute
/// for because it has no way to identify the calling RS.
///
/// <para>Drives the full auth-code+PKCE flow with an RFC-8707 <c>resource=</c>
/// indicator so the assertion is meaningful — anything UserInfo would have
/// emitted in the old (commit 8d85720) shape is provably absent now.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class UserInfoPerAudienceTests : IntegrationTestBase
{
    public UserInfoPerAudienceTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task UserInfo_Is_Pure_Identity_Even_With_Roles_Scope_And_App_Audience()
    {
        // ── Arrange ──────────────────────────────────────────────────────
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write"), ("policy", "admin")]);
        // Audience must be a valid absolute URI per RFC 8707 / OpenIddict
        // server validation. Using a https://-style identifier — same shape
        // any real-world RS would advertise in its discovery.
        const string alphaAudience = "https://alpha-api.example.com";
        var alphaApi = await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);

        // OAuthScope bound to alpha-api as a resource. The ResourceIndicatorHandler
        // only accepts resource= values that are already in principal.GetResources(),
        // and that set comes from scopeManager.ListResourcesAsync(scopes). Without
        // a scope advertising alphaAudience, the resource= parameter is rejected
        // with invalid_target.
        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-spa-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId,
            clientSecret: clientSecret,
            redirectUri: redirectUri,
            appIds: [appAlpha.Id],
            scopes: ["openid", "roles", alphaScopeName]);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Multi",
            lastname: "Aud",
            acronym: "ma",
            email: "ma@test.com",
            password: "TestPass1234");

        // Grant the user a permission scoped to app-alpha: bare action "write"
        // on resource "policy" → expands to "app-alpha:policy:write".
        await GrantAsync(
            testUser.Id,
            roleAppSlug: "app-alpha",
            resourceType: "policy",
            actions: ["write"],
            groupBoundTo: ["app-alpha"]);

        // ── Act: full Authorization Code + PKCE flow ────────────────────
        var accessToken = await DriveAuthCodeFlowAsync(
            username: "ma", password: "TestPass1234",
            clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri,
            scope: $"openid roles {alphaScopeName}",
            resources: [alphaAudience]);

        // ── Act: call /connect/userinfo with the bearer ─────────────────
        var userinfoClient = Factory.CreateClient();
        userinfoClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var userinfoResponse = await userinfoClient.GetAsync("/connect/userinfo",
            TestContext.Current.CancellationToken);
        var userinfoBody = await userinfoResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(userinfoResponse.IsSuccessStatusCode,
            $"/connect/userinfo failed ({(int)userinfoResponse.StatusCode}): {userinfoBody}");

        // ── Assert: identity claims present, authz claims absent ────────
        using var userinfoJson = JsonDocument.Parse(userinfoBody);
        var fullPretty = JsonSerializer.Serialize(userinfoJson, new JsonSerializerOptions { WriteIndented = true });
        var root = userinfoJson.RootElement;

        // Identity slice: sub is mandatory; profile claims arrive when the
        // profile scope is granted (we asked for openid + roles + the app
        // scope, no profile, so only sub is guaranteed here).
        Assert.True(root.TryGetProperty("sub", out _),
            $"sub missing from UserInfo response.\nFull body:\n{fullPretty}");

        // Authz slice: must be absent. Even though the token has the roles
        // scope and a real per-app audience, UserInfo doesn't emit any of
        // these — the RS gets them via the distribution API instead.
        var forbiddenKeys = new[] { "resource_access", "roles", "permissions", "groups" };
        foreach (var key in forbiddenKeys)
        {
            Assert.False(root.TryGetProperty(key, out _),
                $"UserInfo must not emit '{key}' (permission-modell §5 — UserInfo is pure identity).\nFull body:\n{fullPretty}");
        }
    }

    // ─── Authorization Code + PKCE flow helper ───────────────────────────

    private async Task<string> DriveAuthCodeFlowAsync(
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

        using var tokenJson = JsonDocument.Parse(tokenBody);
        return tokenJson.RootElement.GetProperty("access_token").GetString()!;
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

        var id = Guid.NewGuid();
        var (aggregate, created) = OAuthApiAggregate.Create(
            id, name, displayName: name, description: null, enabled: true,
            scopes: Array.Empty<string>());
        session.Events.StartStream<OAuthApiAggregate>(id, created);
        session.Events.Append(id, aggregate.SetAppId(appId));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var loaded = await session.LoadAsync<OAuthApiState>(id, TestContext.Current.CancellationToken);
        return loaded!;
    }

    private async Task CreateOAuthClientAsync(
        string clientId, string clientSecret, string redirectUri, List<Guid> appIds,
        List<string> scopes)
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
