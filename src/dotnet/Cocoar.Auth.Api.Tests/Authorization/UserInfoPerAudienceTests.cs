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
/// End-to-end verification of the per-Audience UserInfo emission introduced
/// in commit 8d85720. Sets up multiple Apps + OAuthApis + a password-grant
/// OAuth client, asks for a multi-aud token via RFC 8707 resource= params,
/// hits /connect/userinfo, and asserts the resource_access shape.
///
/// <para>Why password grant? Authorization-code-flow programmatic replay is
/// possible but heavyweight (browser-redirect simulation). Password grant
/// goes straight from credentials to token with the same RFC-8707 narrowing
/// path — that's enough to exercise the UserInfo emission we want to test.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class UserInfoPerAudienceTests : IntegrationTestBase
{
    public UserInfoPerAudienceTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact(Skip = "WIP — Setup für RFC-8707-OAuth-Code-Flow benötigt zusätzlich OAuthScope-Provisionierung mit Resource-Bindung. Siehe TODO am Ende der Datei. Test-Skelett (Apps, OAuthApis, OAuth-Client, User-Permissions, Auth-Code-Flow + PKCE) ist gebaut und kompiliert; was noch fehlt ist die OAuthScope→Resource-Binding-Verkabelung. Nächste Iteration: OAuthScope mit Resources=[alphaAudience] anlegen und an Client binden, damit ResourceIndicatorHandler den `resource=`-Parameter akzeptiert.")]
    public async Task UserInfo_SingleAud_Emits_PerAudience_ResourceAccess_Block()
    {
        // ── Arrange ──────────────────────────────────────────────────────
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha", resources: ["policy"]);
        // Audience must be a valid absolute URI per RFC 8707 / OpenIddict
        // server validation. Using a https://-style identifier — same shape
        // any real-world RS would advertise in its discovery.
        const string alphaAudience = "https://alpha-api.local";
        var alphaApi = await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-spa-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId,
            clientSecret: clientSecret,
            redirectUri: redirectUri,
            appIds: [appAlpha.Id]);

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
            scope: "openid roles",
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

        // ── Assert: resource_access keyed by audience (NOT app slug) ────
        using var userinfoJson = JsonDocument.Parse(userinfoBody);

        // For diagnostic output if the test fails: dump full response.
        var fullPretty = JsonSerializer.Serialize(userinfoJson, new JsonSerializerOptions { WriteIndented = true });

        Assert.True(userinfoJson.RootElement.TryGetProperty("resource_access", out var resourceAccess),
            $"resource_access missing from UserInfo response.\nFull body:\n{fullPretty}");

        Assert.True(resourceAccess.TryGetProperty(alphaAudience, out var alphaBlock),
            $"resource_access['{alphaAudience}'] missing. resource_access keys: {string.Join(",", resourceAccess.EnumerateObject().Select(p => p.Name))}\nFull body:\n{fullPretty}");

        // permissions: bare 2-segment, slug-stripped
        Assert.True(alphaBlock.TryGetProperty("permissions", out var permissions),
            $"permissions field missing in alpha-api block. Block:\n{alphaBlock}");
        var permList = permissions.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("policy:write", permList);
        Assert.DoesNotContain("app-alpha:policy:write", permList);  // slug must be stripped

        // roles: present
        Assert.True(alphaBlock.TryGetProperty("roles", out var roles));
        Assert.NotEmpty(roles.EnumerateArray());

        // groups: present
        Assert.True(alphaBlock.TryGetProperty("groups", out var groups));
        Assert.NotEmpty(groups.EnumerateArray());
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

    // ─── TODO für nächste Iteration ──────────────────────────────────────
    //
    // Test-Skelett ist gebaut + kompiliert, aber [Skip] gesetzt weil noch
    // ein Setup-Stück fehlt:
    //
    // OAuthScope-Provisionierung mit Resource-Bindung:
    //   - ResourceIndicatorHandler liest principal.GetResources() und
    //     verwirft `resource=`-Parameter die nicht drin sind.
    //   - Resources werden aus Scope→Resource-Mappings befüllt
    //     (scopeManager.ListResourcesAsync via principal.GetScopes()).
    //   - Test muss eine OAuthScope anlegen mit Resources = [alphaAudience]
    //     und sie dem Client zuweisen, plus scope=`<neuer-scope-name>` im
    //     /authorize-Request mitsenden.
    //
    // Lessons learned dieser Session:
    //   - Password-Grant ist server-seitig deaktiviert (OAuth-2.1-Compliance);
    //     Auth-Code + PKCE ist der einzige user-bound Pfad.
    //   - resource= Werte müssen valid absolute URIs sein (RFC-8707-konform).
    //   - OAuthApi.Name (= aud-Claim) sollte URI-Form haben
    //     (z.B. "https://alpha-api.local"), nicht bare "alpha-api".
    //   - Auth-Code-Flow programmatisch funktioniert via:
    //     cookie-login → /authorize → code aus redirect → /token mit
    //     code+verifier+resource=.
    //   - Confidential client + AccessTokenType.Jwt liefert JWT-bearer der
    //     UserInfo-Endpoint validation passieren sollte.

    // ─── Helpers ──────────────────────────────────────────────────────────

    private async Task<App> CreateAppAsync(string slug, string displayName, List<string> resources)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id,
            Slug: slug,
            DisplayName: displayName,
            Description: null,
            Resources: resources,
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
        string clientId, string clientSecret, string redirectUri, List<Guid> appIds)
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
            Scopes = ["openid", "roles"],
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

    private async Task GrantAsync(
        Guid userId, string roleAppSlug, string resourceType,
        IReadOnlyList<string> actions, IReadOnlyList<string> groupBoundTo)
    {
        var role = await Factory.CreateTestRoleAsync(
            name: $"R_{Guid.NewGuid():N}",
            resourceType: resourceType,
            permissions: actions.ToList(),
            appSlug: roleAppSlug);
        await Factory.CreateTestGroupAsync(
            name: $"G_{Guid.NewGuid():N}",
            memberIds: [userId],
            roleIds: [role.Id],
            boundTo: groupBoundTo.ToList());
    }
}
