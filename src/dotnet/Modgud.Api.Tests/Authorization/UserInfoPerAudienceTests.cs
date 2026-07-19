using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Permissions.Abstractions;
using Modgud.Client.AspNetCore;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
        // Include WWW-Authenticate on failure — see note in DriveFlowAndReadAlphaBlockAsync.
        var wwwAuth = string.Join(" | ", response.Headers.WwwAuthenticate.Select(h => $"{h.Scheme} {h.Parameter}"));
        Assert.True(response.IsSuccessStatusCode,
            $"/connect/userinfo after refresh failed ({(int)response.StatusCode}): body='{body}' WWW-Authenticate='{wwwAuth}'");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("resource_access", out var ra)
            && ra.TryGetProperty(alphaAudience, out _),
            $"resource_access['{alphaAudience}'] missing after refresh.\nBody:\n{body}");
    }

    // Issue #124 — reuse of an already-redeemed refresh token (RFC 6749 §10.4) is
    // OpenIddict's own SetRefreshTokenReuseLeeway(TimeSpan.Zero) compromise signal:
    // its stock Protection.ValidateTokenEntry handler rejects the replay and revokes
    // the whole authorization's token family itself (RefreshTokenReuseAuditHandler
    // runs immediately before it in the same ValidateTokenContext pipeline). This was
    // previously silent (no log, no audit trail); it must now land a
    // security.refresh_token_reuse_detected row in the streamless audit store.
    [Fact]
    public async Task RefreshToken_Reuse_RevokesChain_And_RecordsSecurityEvent()
    {
        var appAlpha = await CreateAppAsync("app-reuse", "App Reuse",
            permissions: [("policy", "read")]);
        const string alphaAudience = "https://reuse-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);

        const string alphaScopeName = "reuse-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-reuse-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id],
            scopes: ["openid", "offline_access", alphaScopeName]);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Reuse", lastname: "Detect", acronym: "rd",
            email: "rd@test.com", password: "TestPass1234");

        // authorize → token (offline_access so a refresh token is issued)
        using var tokens = await DriveAuthCodeFlowForTokensAsync(
            username: "rd", password: "TestPass1234",
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            scope: $"openid offline_access {alphaScopeName}",
            resources: [alphaAudience]);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;

        // First redemption is legitimate rotation — marks the token Redeemed.
        await RedeemRefreshTokenAsync(refreshToken, clientId, clientSecret, [alphaAudience]);

        // Second redemption of the SAME (already-redeemed) token is the reuse signal.
        var replayClient = Factory.CreateClient();
        var replayForm = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("resource", alphaAudience),
        };
        var replayResponse = await replayClient.PostAsync(
            "/connect/token", new FormUrlEncodedContent(replayForm), TestContext.Current.CancellationToken);
        var replayBody = await replayResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(replayResponse.IsSuccessStatusCode,
            $"Replayed refresh token should have been rejected: {replayBody}");
        Assert.Contains("invalid_grant", replayBody);

        // The reuse rejection emits a best-effort security event on the async
        // writer — poll briefly for it to land in the system-tenant streamless store.
        var recorded = await PollForSecurityAuditEntryAsync(
            e => e.EventType == AuditEvents.RefreshTokenReuseDetected,
            TestContext.Current.CancellationToken);

        Assert.NotNull(recorded);
        Assert.Equal("Warning", recorded!.Level);
        Assert.Equal("revoked", recorded.Status);
        Assert.Contains(clientId, recorded.Reason ?? "", StringComparison.Ordinal);
        Assert.Equal(testUser.Id.ToString(), recorded.Actor);
    }

    private async Task<SecurityAuditEntry?> PollForSecurityAuditEntryAsync(
        Func<SecurityAuditEntry, bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i < 25; i++)
        {
            await using (var read = GetTenantedDocumentSession("system"))
            {
                var hit = (await read.Query<SecurityAuditEntry>().ToListAsync(ct))
                    .FirstOrDefault(predicate);
                if (hit is not null) return hit;
            }
            await Task.Delay(200, ct);
        }
        return null;
    }

    [Fact]
    public async Task JwtClient_Bakes_ResourceAccess_Into_AccessToken_And_UserInfo_Echoes()
    {
        // Federation v1.1: a JWT-access client has no server-side token payload for
        // UserInfo to read the session-group carrier back from, so the per-audience
        // resource_access (durable ∪ session-derived, computed via the same
        // BuildResourceAccessAsync as the reference path) is baked into the
        // self-contained access token at issuance. This pins the wiring end-to-end:
        // (1) the block is present IN the JWT payload (no UserInfo round-trip
        // needed), and (2) UserInfo echoes that block verbatim rather than silently
        // recomputing a narrower set. (The session-derived union itself is pinned at
        // the service level by FederationV1Phase4Tests; the bake path is identical
        // for durable vs session membership.)
        var slug = "jwtfed-" + Guid.NewGuid().ToString("N")[..8];
        var audience = $"https://{slug}-api.example.com";
        var scopeName = slug + "-api";

        var app = await CreateAppAsync(slug, "Jwt Fed App",
            permissions: [("policy", "read"), ("policy", "write"), ("policy", "admin")]);
        await CreateOAuthApiAsync(audience, app.Id);
        await CreateScopeAsync(name: scopeName, resources: [audience], appId: app.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-jwt-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [app.Id], scopes: ["openid", "roles", "permissions", scopeName],
            accessTokenType: AccessTokenType.Jwt);

        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Jwt", lastname: "Fed", acronym: "jf", email: "jf@test.com", password: "TestPass1234");
        await GrantAsync(user.Id, roleAppSlug: slug, resourceType: "policy",
            actions: ["write"], groupBoundTo: [slug]);

        var accessToken = await DriveAuthCodeFlowAsync(
            username: "jf", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: $"openid roles permissions {scopeName}", resources: [audience]);

        // (1) resource_access is baked into the self-contained JWT.
        var payload = DecodeJwtPayload(accessToken);
        Assert.True(payload.TryGetProperty("resource_access", out var ra),
            $"resource_access missing from JWT payload:\n{payload}");
        Assert.True(ra.TryGetProperty(audience, out var block),
            $"resource_access['{audience}'] missing from JWT. keys: {string.Join(",", ra.EnumerateObject().Select(p => p.Name))}");
        var permsInToken = block.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("policy:write", permsInToken);
        Assert.DoesNotContain("policy:read", permsInToken);

        // (2) UserInfo echoes the same block.
        var userinfoClient = Factory.CreateClient();
        userinfoClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await userinfoClient.GetAsync("/connect/userinfo", TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/userinfo failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        var uiPerms = doc.RootElement.GetProperty("resource_access").GetProperty(audience)
            .GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("policy:write", uiPerms);
        Assert.DoesNotContain("policy:read", uiPerms);
    }

    [Fact]
    public async Task JwtClient_BakedResourceAccess_Honours_Requested_Audience_Only()
    {
        // The baked block must match the token's narrowed aud (RFC 8707 resource
        // indicators), never the broader scope-derived set — otherwise a block for
        // an audience the client didn't request would ride along in the token.
        var aSlug = "jwtaud-a-" + Guid.NewGuid().ToString("N")[..8];
        var bSlug = "jwtaud-b-" + Guid.NewGuid().ToString("N")[..8];
        var aAud = $"https://{aSlug}.example.com";
        var bAud = $"https://{bSlug}.example.com";

        var appA = await CreateAppAsync(aSlug, "App A", permissions: [("policy", "write")]);
        var appB = await CreateAppAsync(bSlug, "App B", permissions: [("widget", "write")]);
        await CreateOAuthApiAsync(aAud, appA.Id);
        await CreateOAuthApiAsync(bAud, appB.Id);
        await CreateScopeAsync(name: aSlug + "-api", resources: [aAud], appId: appA.Id);
        await CreateScopeAsync(name: bSlug + "-api", resources: [bAud], appId: appB.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-jwt-2aud-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appA.Id, appB.Id],
            scopes: ["openid", "roles", "permissions", aSlug + "-api", bSlug + "-api"],
            accessTokenType: AccessTokenType.Jwt);

        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Two", lastname: "Aud", acronym: "ta", email: "ta@test.com", password: "TestPass1234");
        await GrantAsync(user.Id, roleAppSlug: aSlug, resourceType: "policy", actions: ["write"], groupBoundTo: [aSlug]);
        await GrantAsync(user.Id, roleAppSlug: bSlug, resourceType: "widget", actions: ["write"], groupBoundTo: [bSlug]);

        // Request ONLY audience A as the resource indicator.
        var accessToken = await DriveAuthCodeFlowAsync(
            username: "ta", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri,
            scope: $"openid roles permissions {aSlug}-api {bSlug}-api", resources: [aAud]);

        var payload = DecodeJwtPayload(accessToken);
        Assert.True(payload.TryGetProperty("resource_access", out var ra),
            $"resource_access missing from JWT payload:\n{payload}");
        Assert.True(ra.TryGetProperty(aAud, out _), "audience A block expected (it was requested).");
        Assert.False(ra.TryGetProperty(bAud, out _),
            "audience B block must NOT appear — it was not in the requested resource set.");
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.True(parts.Length >= 2, $"not a JWT (reference token?): {jwt[..Math.Min(16, jwt.Length)]}…");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    [Fact]
    public async Task JwtClient_Federated_SessionGroup_Lands_In_AccessToken()
    {
        // The user is NOT a durable member of the group — it enters authz ONLY via
        // the session-group carrier on the (federated) cookie. Proves the full
        // carrier path cookie → /connect/authorize grant → JWT bake, end-to-end
        // through the real HTTP pipeline (not just durable authz, and not just the
        // service-level union pinned by FederationV1Phase4Tests).
        var slug = "fedjwt-" + Guid.NewGuid().ToString("N")[..8];
        var audience = $"https://{slug}-api.example.com";
        var scopeName = slug + "-api";

        var app = await CreateAppAsync(slug, "Fed JWT App", permissions: [("policy", "read"), ("policy", "write")]);
        await CreateOAuthApiAsync(audience, app.Id);
        await CreateScopeAsync(name: scopeName, resources: [audience], appId: app.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-fedjwt-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [app.Id], scopes: ["openid", "roles", "permissions", scopeName],
            accessTokenType: AccessTokenType.Jwt);

        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Fed", lastname: "Jwt", acronym: "fj", email: "fj@test.com", password: "TestPass1234");
        var role = await Factory.CreateTestRoleAsync($"R_{Guid.NewGuid():N}", [("policy", "write")], appSlug: slug);
        var sessionGroup = await Factory.CreateTestGroupAsync(
            $"SG_{Guid.NewGuid():N}", memberIds: [], roleIds: [role.Id], boundTo: [slug]);

        // Control: a plain (password) login carries no carrier → durable-only →
        // the session group's permission must be absent.
        var plainToken = await DriveAuthCodeFlowAsync(
            username: "fj", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: $"openid roles permissions {scopeName}", resources: [audience]);
        var plain = DecodeJwtPayload(plainToken);
        var plainHasWrite =
            plain.TryGetProperty("resource_access", out var pra)
            && pra.TryGetProperty(audience, out var pb)
            && pb.TryGetProperty("permissions", out var pp)
            && pp.EnumerateArray().Any(e => e.GetString() == "policy:write");
        Assert.False(plainHasWrite, "without the carrier, policy:write must NOT appear (durable membership is empty).");

        // Federated: the forged cookie carries the session group → policy:write appears.
        var fedClient = await CreateFederatedCookieClientAsync("fj", sessionGroup.Id);
        var fedToken = await DriveAuthCodeFlowAsync(
            username: "fj", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: $"openid roles permissions {scopeName}", resources: [audience],
            cookieClient: fedClient);

        var perms = DecodeJwtPayload(fedToken).GetProperty("resource_access").GetProperty(audience)
            .GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("policy:write", perms);   // came ONLY from the session-group carrier
    }

    [Fact]
    public async Task ReferenceClient_Federated_SessionGroup_Surfaces_At_UserInfo()
    {
        // Reference client: the carrier rides the server-side reference token and
        // the session-derived permission surfaces at /connect/userinfo (the path
        // the ID2090 hotfix repaired). User is NOT a durable member.
        var slug = "fedref-" + Guid.NewGuid().ToString("N")[..8];
        var audience = $"https://{slug}-api.example.com";
        var scopeName = slug + "-api";

        var app = await CreateAppAsync(slug, "Fed Ref App", permissions: [("policy", "read"), ("policy", "write")]);
        await CreateOAuthApiAsync(audience, app.Id);
        await CreateScopeAsync(name: scopeName, resources: [audience], appId: app.Id);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-fedref-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [app.Id], scopes: ["openid", "roles", "permissions", scopeName],
            accessTokenType: AccessTokenType.Reference);

        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Fed", lastname: "Ref", acronym: "fr", email: "fr@test.com", password: "TestPass1234");
        var role = await Factory.CreateTestRoleAsync($"R_{Guid.NewGuid():N}", [("policy", "write")], appSlug: slug);
        var sessionGroup = await Factory.CreateTestGroupAsync(
            $"SG_{Guid.NewGuid():N}", memberIds: [], roleIds: [role.Id], boundTo: [slug]);

        var fedClient = await CreateFederatedCookieClientAsync("fr", sessionGroup.Id);
        var accessToken = await DriveAuthCodeFlowAsync(
            username: "fr", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: $"openid roles permissions {scopeName}", resources: [audience],
            cookieClient: fedClient);

        var userinfoClient = Factory.CreateClient();
        userinfoClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await userinfoClient.GetAsync("/connect/userinfo", TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/userinfo failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        var perms = doc.RootElement.GetProperty("resource_access").GetProperty(audience)
            .GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("policy:write", perms);   // session-group permission via the server-side carrier
    }

    /// <summary>
    /// Forges a valid ApplicationScheme auth cookie carrying the federation
    /// session-group carrier claim(s), without a real upstream-IdP round-trip.
    /// Uses the app's own <see cref="SignInManager{T}"/> principal (valid security
    /// stamp) + the real <c>TicketDataFormat</c>, protected under the system tenant
    /// so the request pipeline (also system tenant) accepts it. This stubs only the
    /// deriver→cookie link (covered by FederationV1Phase3Tests); everything
    /// downstream — cookie→grant→token/UserInfo — runs for real.
    /// </summary>
    private async Task<HttpClient> CreateFederatedCookieClientAsync(string userName, params Guid[] sessionGroupIds)
    {
        using var scope = Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();

        var user = await userManager.FindByNameAsync(userName)
            ?? throw new InvalidOperationException($"user '{userName}' not found");
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var gid in sessionGroupIds)
            identity.AddClaim(new Claim(FederationClaimTypes.SessionGroup, gid.ToString()));

        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            },
            IdentityConstants.ApplicationScheme);

        // Protect under the system tenant so the request pipeline (system tenant
        // by default in tests) resolves the same per-tenant DataProtection keys.
        string cookieValue;
        using (TenantContext.Enter(TenantConstants.SystemTenantId))
            cookieValue = cookieOptions.TicketDataFormat.Protect(ticket);

        var handler = new CookieContainerHandler();
        handler.Seed(new Uri("http://localhost"), cookieOptions.Cookie.Name!, cookieValue);
        return Factory.CreateDefaultClient(handler);
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

    [Fact]
    public async Task Introspection_Carries_ResourceAccess_Only_For_Audience_Or_Presenter_Client()
    {
        // #132 step 1 — pin whether /connect/introspect echoes the per-audience
        // resource_access block, and to which callers. This decides the
        // reference-token client-lib design: if a resource server can introspect
        // and read the permission block in one call, the lib needs no separate
        // /connect/userinfo round-trip. Nothing in-repo pinned this before (the
        // #132 issue explicitly flags the gap).
        //
        // OpenIddict's stock introspection handler only reveals a token — at all,
        // including active:true and any non-standard claims — to a caller that is
        // one of the token's audiences or its authorized presenter (azp). We pin
        // three caller identities:
        //   A. a separate RS client (neither aud nor azp)        → active:false
        //   B. the token's own presenter client (azp)            → active + block
        //   C. a client whose client_id == the audience URL      → active + block
        // C is the Modgud-idiomatic RS identity: the audience is the RS URL, which
        // RFC 8707 already put in the token's aud, so a client registered under
        // that same id is authorised to introspect and receives resource_access
        // in a single call.
        var appAlpha = await CreateAppAsync("app-alpha", "App Alpha",
            permissions: [("policy", "read"), ("policy", "write"), ("policy", "admin")]);
        const string alphaAudience = "https://alpha-api.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);
        const string alphaScopeName = "alpha-api";
        await CreateScopeAsync(name: alphaScopeName, resources: [alphaAudience], appId: appAlpha.Id);

        // The user-facing client that obtains an opaque REFERENCE token.
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-ref-introspect-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [appAlpha.Id], scopes: ["openid", "roles", "permissions", alphaScopeName],
            accessTokenType: AccessTokenType.Reference);

        // A separate confidential client standing in for the resource server
        // calling /connect/introspect with its own credentials.
        var rsSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var rsClientId = "test-rs-introspector-" + Guid.NewGuid().ToString("N");
        await CreateOAuthClientAsync(
            clientId: rsClientId, clientSecret: rsSecret, redirectUri: "http://localhost/rs-callback",
            appIds: [appAlpha.Id], scopes: ["openid"]);

        var testUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Introspect", lastname: "Ref", acronym: "ir",
            email: "ir@test.com", password: "TestPass1234");
        await GrantAsync(testUser.Id, roleAppSlug: "app-alpha", resourceType: "policy",
            actions: ["write"], groupBoundTo: ["app-alpha"]);

        var accessToken = await DriveAuthCodeFlowAsync(
            username: "ir", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: $"openid roles permissions {alphaScopeName}",
            resources: [alphaAudience]);

        // Identity C — a confidential client whose client_id IS the audience URL
        // (the RS URL is already in the token's aud via RFC 8707 resource=).
        var audSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        await CreateOAuthClientAsync(
            clientId: alphaAudience, clientSecret: audSecret, redirectUri: "http://localhost/aud-callback",
            appIds: [appAlpha.Id], scopes: ["openid"]);

        // A — a stranger client (neither aud nor azp) can't introspect at all.
        var bodyRs = await IntrospectAsync(rsClientId, rsSecret, accessToken);
        using (var docA = JsonDocument.Parse(bodyRs))
            Assert.False(docA.RootElement.GetProperty("active").GetBoolean(),
                $"A client that is neither audience nor presenter must get active:false.\n{bodyRs}");

        // B — the token's own presenter sees active:true + the full block.
        var bodyPresenter = await IntrospectAsync(clientId, clientSecret, accessToken);
        AssertActiveWithWritePermission(bodyPresenter, alphaAudience);

        // C — a client whose client_id == the audience URL likewise sees it
        // (form-body auth: a URL client_id collides with HTTP Basic's colon).
        var bodyAud = await IntrospectAsync(alphaAudience, audSecret, accessToken, bodyAuth: true);
        AssertActiveWithWritePermission(bodyAud, alphaAudience);
    }

    private static void AssertActiveWithWritePermission(string introspectionBody, string audience)
    {
        using var doc = JsonDocument.Parse(introspectionBody);
        Assert.True(doc.RootElement.TryGetProperty("active", out var active) && active.GetBoolean(),
            $"token should introspect as active:\n{introspectionBody}");
        Assert.True(
            doc.RootElement.TryGetProperty("resource_access", out var ra)
                && ra.TryGetProperty(audience, out var block)
                && block.TryGetProperty("permissions", out var perms)
                && perms.EnumerateArray().Any(e => e.GetString() == "policy:write"),
            $"introspection must carry resource_access['{audience}'].permissions incl. policy:write.\n{introspectionBody}");
    }

    private async Task<string> IntrospectAsync(
        string clientId, string clientSecret, string token, bool bodyAuth = false)
    {
        var client = Factory.CreateClient();
        var form = new List<KeyValuePair<string, string>>
        {
            new("token", token),
            new("token_type_hint", "access_token"),
        };
        if (bodyAuth)
        {
            form.Add(new("client_id", clientId));
            form.Add(new("client_secret", clientSecret));
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (!bodyAuth)
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        }
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/introspect failed ({(int)resp.StatusCode}): {body}");
        return body;
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
        // Include WWW-Authenticate on failure — OpenIddict reports the real reason there
        // (e.g. ID2090 "signing key not found"), not in the (empty) 401 body.
        var wwwAuth = string.Join(" | ", response.Headers.WwwAuthenticate.Select(h => $"{h.Scheme} {h.Parameter}"));
        Assert.True(response.IsSuccessStatusCode,
            $"/connect/userinfo failed ({(int)response.StatusCode}): body='{body}' WWW-Authenticate='{wwwAuth}'");

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
        IReadOnlyList<string> resources,
        HttpClient? cookieClient = null)
    {
        using var json = await DriveAuthCodeFlowForTokensAsync(
            username, password, clientId, clientSecret, redirectUri, scope, resources, cookieClient);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Same as <see cref="DriveAuthCodeFlowAsync"/> but returns the full
    /// token-endpoint JSON response (so callers can read the refresh_token too).
    /// When <paramref name="cookieClient"/> is supplied it is used for the
    /// /connect/authorize step instead of a fresh password login — the federated
    /// tests pass a client carrying a hand-forged cookie with the session-group
    /// carrier.</summary>
    private async Task<JsonDocument> DriveAuthCodeFlowForTokensAsync(
        string username, string password,
        string clientId, string clientSecret,
        string redirectUri,
        string scope,
        IReadOnlyList<string> resources,
        HttpClient? cookieClient = null)
    {
        // 1. Cookie-login first so /connect/authorize sees an authenticated principal.
        cookieClient ??= await CreateAuthenticatedClientAsync(username, password);

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

    // ── #139: end-to-end reference-token resource server (client library) ───────

    [Fact]
    public async Task ReferenceToken_ResourceServer_Gates_On_Introspected_Permission()
    {
        // #139 — the runnable reference-token sample's path, proven end-to-end
        // through the client library: an opaque access token is validated by
        // `AddModgudReferenceTokenClient` via /connect/introspect, the per-audience
        // resource_access block is projected onto the principal, and a
        // `RequiresModgudPermission` gate does exact-match. The IdP side (which
        // callers get resource_access) is pinned separately by
        // Introspection_Carries_ResourceAccess_Only_For_Audience_Or_Presenter_Client;
        // this pins the resource-server half.
        var app = await CreateAppAsync("rs-refapp", "RS Reference App",
            permissions: [("policy", "read"), ("policy", "admin")]);
        const string audience = "https://rs-reftoken.example.com";
        await CreateOAuthApiAsync(audience, app.Id);
        const string scopeName = "rs-reftoken-api";
        await CreateScopeAsync(scopeName, [audience], app.Id);

        // The user-facing client that obtains an opaque REFERENCE token.
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-refrs-" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(
            clientId, clientSecret, redirectUri, [app.Id],
            ["openid", "roles", "permissions", scopeName], AccessTokenType.Reference);

        // The resource server's introspection client — client_id == its audience
        // (the OAuthApi name, already in the token's aud via RFC 8707), the setup
        // the docs describe.
        var introspectionSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        await CreateOAuthClientAsync(
            audience, introspectionSecret, "http://localhost/rs-callback", [app.Id], ["openid"]);

        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Ref", lastname: "RS", acronym: "rr", email: "rr@test.com", password: "TestPass1234");
        // Grant policy:read only — policy:admin stays absent so the gate can be seen to deny.
        await GrantAsync(user.Id, roleAppSlug: "rs-refapp", resourceType: "policy",
            actions: ["read"], groupBoundTo: ["rs-refapp"]);

        // The client is configured for opaque reference tokens; the resource server
        // below validates whatever it receives via /connect/introspect regardless of
        // the token's on-the-wire format, which is exactly the reference-mode path.
        var referenceToken = await DriveAuthCodeFlowAsync(
            username: "rr", password: "TestPass1234", clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: $"openid roles permissions {scopeName}", resources: [audience]);

        // Point the library's introspection HttpClient at the in-memory IdP (its
        // Authority is http://localhost, so the introspection Host resolves to the
        // same realm the fixtures were created in).
        ModgudTokenIntrospection.SharedClient = Factory.CreateClient();

        using var rsHost = await BuildReferenceTokenResourceServerAsync(audience, introspectionSecret);
        var rs = rsHost.GetTestClient();

        // Granted permission → 200.
        Assert.Equal(HttpStatusCode.OK, (await SendWithTokenAsync(rs, "/policy/read", referenceToken)).StatusCode);

        // The principal carries the flattened permission.
        var meResp = await SendWithTokenAsync(rs, "/me", referenceToken);
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var meBody = await meResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var meDoc = JsonDocument.Parse(meBody))
            Assert.Contains(meDoc.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()),
                p => p == "policy:read");

        // Missing permission → 403 (authenticated, but not granted policy:admin).
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithTokenAsync(rs, "/policy/admin", referenceToken)).StatusCode);

        // No credentials → 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await rs.GetAsync("/policy/read", TestContext.Current.CancellationToken)).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWithTokenAsync(HttpClient client, string path, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Boots a minimal in-memory resource-server host over the published client
    /// library exactly as the reference-token sample (Modgud.TestApps.ResourceApi
    /// with TESTAPPS:TOKENMODE=reference) does — <c>AddModgudReferenceTokenClient</c>
    /// plus <c>RequiresModgudPermission</c> gates — so the opaque-token path is
    /// exercised end-to-end against the in-memory IdP.
    /// </summary>
    private static async Task<IHost> BuildReferenceTokenResourceServerAsync(string audience, string introspectionSecret)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services
                        .AddAuthentication(ModgudReferenceTokenDefaults.AuthenticationScheme)
                        .AddModgudReferenceTokenClient(o =>
                        {
                            o.Authority = "http://localhost"; // introspection Host = the test realm
                            o.Audience = audience;            // == the introspection client_id
                            o.IntrospectionClientSecret = introspectionSecret;
                        });
                    services.AddAuthorization();
                })
                .Configure(builder =>
                {
                    builder.UseRouting();
                    builder.UseAuthentication();
                    builder.UseAuthorization();
                    builder.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
                        {
                            permissions = user.FindAll(ModgudClaimsTransformation.PermissionClaimType)
                                .Select(c => c.Value).ToArray(),
                        })).RequireAuthorization();

                        endpoints.MapGet("/policy/read", () => Results.Ok())
                            .RequireAuthorization().RequiresModgudPermission("policy:read");

                        endpoints.MapGet("/policy/admin", () => Results.Ok())
                            .RequireAuthorization().RequiresModgudPermission("policy:admin");
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }
}
