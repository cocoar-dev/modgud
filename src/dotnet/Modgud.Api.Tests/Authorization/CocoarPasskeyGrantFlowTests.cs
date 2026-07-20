using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end verification of the ADR-0010 Phase-2 native (cookieless) passkey
/// flow: the anonymous <c>POST /connect/passkey/begin</c> ceremony endpoint and
/// the <c>urn:cocoar:passkey</c> token grant. The crypto SUCCESS path needs a
/// real authenticator (a signed assertion over the server challenge) and is NOT
/// integration-testable without a software authenticator — these tests cover
/// everything else: the begin endpoint + per-realm gate, the two grant gates
/// (per-realm flag, per-client gt: permission), the uniform <c>invalid_grant</c>
/// on bad/missing/expired ceremonies, and single-use ceremony consumption.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class CocoarPasskeyGrantFlowTests : IntegrationTestBase
{
    public CocoarPasskeyGrantFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ─────────────────────────────── begin endpoint ───────────────────────────

    [Fact]
    public async Task Begin_FlagOn_ReturnsCeremonyAndChallenge()
    {
        await EnableNativeGrantsAsync();

        var anon = Factory.CreateClient();
        var resp = await anon.PostAsync("/connect/passkey/begin", content: null, TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/passkey/begin failed ({(int)resp.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        Assert.True(Guid.TryParse(json.RootElement.GetProperty("ceremonyId").GetString(), out var ceremonyId));
        Assert.NotEqual(Guid.Empty, ceremonyId);
        // The verbatim FIDO2 options must carry a challenge + the discoverable
        // (empty allowCredentials) + UV=required shape.
        var options = json.RootElement.GetProperty("options");
        Assert.False(string.IsNullOrEmpty(options.GetProperty("challenge").GetString()));

        // A single-use ceremony doc was persisted for the realm.
        Assert.True(await CeremonyIsRedeemableAsync(ceremonyId));
    }

    [Fact]
    public async Task Begin_FlagOff_Rejected()
    {
        // NativeGrants left at its default (OFF).
        var anon = Factory.CreateClient();
        var resp = await anon.PostAsync("/connect/passkey/begin", content: null, TestContext.Current.CancellationToken);

        Assert.False(resp.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ─────────────────────────────── grant gates ──────────────────────────────

    [Fact]
    public async Task PasskeyGrant_RealmFlagOff_Rejected()
    {
        // Flag OFF; client DOES carry gt:urn:cocoar:passkey so we reach the
        // in-handler realm gate (not OpenIddict's per-client gate).
        await SeedPasskeyClientAsync("native-passkey-app");

        var response = await PostPasskeyAsync("native-passkey-app", Guid.NewGuid().ToString(), "{}");

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("unsupported_grant_type", body);
    }

    [Fact]
    public async Task PasskeyGrant_ClientWithoutPermission_Rejected()
    {
        await EnableNativeGrantsAsync();
        // Client with only standard grants — no gt:urn:cocoar:passkey.
        await SeedClientAsync("plain-passkey-app", ["authorization_code", "refresh_token"]);

        var response = await PostPasskeyAsync("plain-passkey-app", Guid.NewGuid().ToString(), "{}");

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("unauthorized_client", body);
    }

    [Fact]
    public async Task PasskeyGrant_MissingParams_InvalidGrant()
    {
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Passkey,
            ["client_id"] = "native-passkey-app",
            ["client_secret"] = "native-passkey-app-secret",
            ["scope"] = "openid",
            // no ceremony_id, no assertion
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
    }

    [Fact]
    public async Task PasskeyGrant_UnknownCeremony_InvalidGrant()
    {
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");

        var response = await PostPasskeyAsync("native-passkey-app", Guid.NewGuid().ToString(), "{}");

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
    }

    [Fact]
    public async Task PasskeyGrant_ExpiredCeremony_InvalidGrant_AndConsumed()
    {
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");
        var ceremonyId = await SeedCeremonyAsync(expired: true);

        var response = await PostPasskeyAsync("native-passkey-app", ceremonyId.ToString(), "{}");

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
        // An expired ceremony is consumed (deleted) on the rejected path.
        Assert.False(await CeremonyIsRedeemableAsync(ceremonyId));
    }

    [Fact]
    public async Task PasskeyGrant_BogusAssertion_InvalidGrant_AndConsumesCeremony()
    {
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");
        // A live (non-expired) ceremony with a bogus assertion: the ceremony is
        // single-use — consumed even though verification fails.
        var ceremonyId = await SeedCeremonyAsync(expired: false);

        var response = await PostPasskeyAsync("native-passkey-app", ceremonyId.ToString(), "{}");

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
        Assert.False(await CeremonyIsRedeemableAsync(ceremonyId));
    }

    [Fact]
    public async Task PasskeyGrant_MalformedAssertionMatchingCredential_InvalidGrant_NotServerError()
    {
        // Regression for the verifier fail-closed contract: an assertion whose id
        // matches a stored credential but which has NO "response" member reaches
        // FIDO2 MakeAssertion and used to throw (NRE) → HTTP 500. It must fail
        // closed as invalid_grant instead.
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");

        var credentialId = RandomNumberGenerator.GetBytes(32);
        await SeedCredentialAsync(credentialId);
        var ceremonyId = await SeedCeremonyAsync(expired: false);

        var idB64Url = Convert.ToBase64String(credentialId).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var assertion = $"{{\"id\":\"{idB64Url}\",\"type\":\"public-key\"}}"; // no "response"

        var response = await PostPasskeyAsync("native-passkey-app", ceremonyId.ToString(), assertion);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
        // The ceremony is single-use — consumed even on this rejected path.
        Assert.False(await CeremonyIsRedeemableAsync(ceremonyId));
    }

    // ─────────────────── crypto success path (software authenticator) ─────────

    [Fact]
    public async Task PasskeyGrant_ValidAssertion_NativeOrigin_MintsTokens()
    {
        // The full crypto-success path: a real ES256 assertion over the server
        // challenge, presented with the native origin https://<rp-id>. Closes
        // ADR-0010 Gate item #2 (native-origin acceptance pinned by a test).
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle);

        var (ceremonyId, challenge, rpId) = await BeginAsync();
        var assertion = authenticator.CreateAssertionJson(challenge, rpId, $"https://{rpId}");

        var response = await PostPasskeyAsync("native-passkey-app", ceremonyId, assertion);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"passkey grant failed ({(int)response.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        Assert.Equal(DefaultUser!.Id.ToString(), jwt.Subject);
        Assert.True(json.RootElement.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()));
        Assert.False(response.Headers.Contains("Set-Cookie"));

        // The ceremony is single-use — no longer redeemable, even on success.
        Assert.False(await CeremonyIsRedeemableAsync(Guid.Parse(ceremonyId)));
    }

    [Fact]
    public async Task PasskeyGrant_ValidSignature_WrongOrigin_Rejected()
    {
        // Same valid signature, but a FOREIGN origin: the WebAuthn origin/RP-ID
        // check must reject it (this is the anti-phishing property the native
        // grant relies on). Pins that the origin gate actually fires.
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle);

        var (ceremonyId, challenge, rpId) = await BeginAsync();
        var assertion = authenticator.CreateAssertionJson(challenge, rpId, "https://evil.example");

        var response = await PostPasskeyAsync("native-passkey-app", ceremonyId, assertion);

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);
    }

    // ─────────────────── per-client RP-ID (ADR-0009) ──────────────────────────

    [Fact]
    public async Task PasskeyGrant_PerClientRpId_ValidAssertion_MintsTokens()
    {
        // ADR-0009 — a credential enrolled under a client's per-client RP-ID logs in
        // for that client. Proves the override path end-to-end AND that the
        // candidate filter does not over-block a legitimately-matching credential.
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("app-b", rpId: "b.localhost");

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle, rpId: "b.localhost");

        var (ceremonyId, challenge, rpId) = await BeginAsync(clientId: "app-b");
        Assert.Equal("b.localhost", rpId); // resolver applied the per-client RP-ID
        var assertion = authenticator.CreateAssertionJson(challenge, rpId, $"https://{rpId}");

        var response = await PostPasskeyAsync("app-b", ceremonyId, assertion);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"per-client passkey grant failed ({(int)response.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("access_token").GetString()!);
        Assert.Equal(DefaultUser!.Id.ToString(), jwt.Subject);
    }

    [Fact]
    public async Task PasskeyGrant_CredentialEnrolledUnderClientA_RedeemedForClientB_InvalidGrant()
    {
        // ADR-0009 Gate #3 — cross-app credential confusion. A credential enrolled
        // under client A's RP-ID must NEITHER surface NOR verify when client B (a
        // different RP-ID) redeems. Both layers reject: the candidate filter excludes
        // the A-credential, and the FIDO2 rpIdHash/origin check would too. The
        // A-credential must not even be touched (its clone-detection counter stays put).
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("app-a", rpId: "a.localhost");
        await SeedPasskeyClientAsync("app-b", rpId: "b.localhost");

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle, rpId: "a.localhost");

        // Begin for client B; present a GENUINE assertion signed for A's RP-ID/origin.
        var (ceremonyId, challenge, rpId) = await BeginAsync(clientId: "app-b");
        Assert.Equal("b.localhost", rpId);
        var assertion = authenticator.CreateAssertionJson(challenge, "a.localhost", "https://a.localhost");

        var response = await PostPasskeyAsync("app-b", ceremonyId, assertion);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);

        // The A-credential was neither surfaced nor counter-advanced.
        var cred = await LoadCredentialAsync(authenticator.CredentialId);
        Assert.NotNull(cred);
        Assert.Equal(0u, cred!.SignatureCount);
    }

    // ─────────────────── native enrollment (ADR-0009) ─────────────────────────

    [Fact]
    public async Task NativeEnroll_StoresCredentialUnderClientRpId_AndCanLogin()
    {
        // ADR-0009 full bootstrap: authenticate once, then add a passkey for THIS
        // app via the Bearer enroll endpoints — the new credential is stored under
        // the client's per-client RP-ID and is usable for a subsequent native login.
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("app-a", rpId: "a.localhost");

        // Bootstrap login (existing credential) → access token for app-a.
        using var bootstrap = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(bootstrap.CredentialId, bootstrap.CosePublicKey(), bootstrap.UserHandle, rpId: "a.localhost");
        var (bootCeremony, bootChallenge, bootRpId) = await BeginAsync(clientId: "app-a");
        var bootAssertion = bootstrap.CreateAssertionJson(bootChallenge, bootRpId, $"https://{bootRpId}");
        var tokenResp = await PostPasskeyAsync("app-a", bootCeremony, bootAssertion);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.IsSuccessStatusCode, $"bootstrap login failed ({(int)tokenResp.StatusCode}): {tokenBody}");
        var accessToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;

        // Enroll a NEW credential via the Bearer endpoints. The enroll ceremony sets
        // the WebAuthn user handle to UTF8(user.Id) (Fido2User.Id), so the software
        // authenticator must echo exactly that at login (the owner-check compares them).
        using var enrolling = new SoftwareWebAuthnAuthenticator(System.Text.Encoding.UTF8.GetBytes(DefaultUser!.Id.ToString()));
        var bearer = Factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        var beginResp = await bearer.PostAsync("/connect/passkey/enroll/begin", content: null, TestContext.Current.CancellationToken);
        var beginBody = await beginResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var wwwAuth = beginResp.Headers.WwwAuthenticate.ToString();
        Assert.True(beginResp.IsSuccessStatusCode, $"enroll/begin failed ({(int)beginResp.StatusCode}): [{wwwAuth}] {beginBody}");
        using var beginJson = JsonDocument.Parse(beginBody);
        var enrollCeremonyId = beginJson.RootElement.GetProperty("ceremonyId").GetString()!;
        var opts = beginJson.RootElement.GetProperty("options");
        var enrollChallenge = opts.GetProperty("challenge").GetString()!;
        var enrollRpId = opts.GetProperty("rp").GetProperty("id").GetString()!;
        Assert.Equal("a.localhost", enrollRpId); // resolved from the token's client

        var attestation = enrolling.CreateAttestationJson(enrollChallenge, enrollRpId, $"https://{enrollRpId}");
        var enrollReqBody = $"{{\"ceremonyId\":\"{enrollCeremonyId}\",\"attestation\":{attestation}}}";
        var enrollResp = await bearer.PostAsync("/connect/passkey/enroll",
            new StringContent(enrollReqBody, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var enrollRespBody = await enrollResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(enrollResp.IsSuccessStatusCode, $"enroll failed ({(int)enrollResp.StatusCode}): {enrollRespBody}");

        // Stored under the client's RP-ID.
        var stored = await LoadCredentialAsync(enrolling.CredentialId);
        Assert.NotNull(stored);
        Assert.Equal("a.localhost", stored!.RpId);

        // And usable for a subsequent native login for that client.
        var (loginCeremony, loginChallenge, loginRpId) = await BeginAsync(clientId: "app-a");
        var loginAssertion = enrolling.CreateAssertionJson(loginChallenge, loginRpId, $"https://{loginRpId}");
        var loginResp = await PostPasskeyAsync("app-a", loginCeremony, loginAssertion);
        var loginBody = await loginResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(loginResp.IsSuccessStatusCode, $"login with enrolled credential failed ({(int)loginResp.StatusCode}): {loginBody}");
    }

    [Fact]
    public async Task NativeEnrollBegin_Anonymous_Unauthorized()
    {
        await EnableNativeGrantsAsync();
        var anon = Factory.CreateClient();
        var resp = await anon.PostAsync("/connect/passkey/enroll/begin", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ─────────────────────────────── helpers ──────────────────────────────────

    private async Task<(string CeremonyId, string Challenge, string RpId)> BeginAsync(string? clientId = null)
    {
        var anon = Factory.CreateClient();
        HttpContent? content = clientId is null
            ? null
            : new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId });
        var resp = await anon.PostAsync("/connect/passkey/begin", content, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/passkey/begin failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        var ceremonyId = json.RootElement.GetProperty("ceremonyId").GetString()!;
        var options = json.RootElement.GetProperty("options");
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rpId").GetString()!;
        return (ceremonyId, challenge, rpId);
    }

    private Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form) =>
        Factory.CreateClient().PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> PostPasskeyAsync(string clientId, string ceremonyId, string assertion) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Passkey,
            ["client_id"] = clientId,
            ["client_secret"] = $"{clientId}-secret",
            ["ceremony_id"] = ceremonyId,
            ["assertion"] = assertion,
            ["scope"] = "openid email profile offline_access",
        });

    private async Task EnableNativeGrantsAsync()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedCeremonyAsync(bool expired)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var ceremony = new PasskeyCeremony
        {
            Id = Guid.NewGuid(),
            OptionsJson = "{}",
            ExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ceremony.Id;
    }

    private async Task SeedCredentialAsync(byte[] credentialId, byte[]? publicKey = null, byte[]? userHandle = null, string? rpId = null)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new StoredPasskeyCredential
        {
            Id = Guid.NewGuid(),
            UserId = DefaultUser!.Id,
            CredentialId = credentialId,
            PublicKey = publicKey ?? RandomNumberGenerator.GetBytes(64),
            UserHandle = userHandle ?? DefaultUser.Id.ToByteArray(),
            SignatureCount = 0,
            AttestationType = "none",
            DisplayName = "Test passkey",
            RpId = rpId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<StoredPasskeyCredential?> LoadCredentialAsync(byte[] credentialId)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var all = await session.Query<StoredPasskeyCredential>().ToListAsync(TestContext.Current.CancellationToken);
        return all.FirstOrDefault(c => c.CredentialId.SequenceEqual(credentialId));
    }

    /// <summary>
    /// Asserts the security property (the ceremony can still be redeemed) rather
    /// than the storage mechanism. Consuming is a version-checked Store of
    /// <c>ConsumedAt</c>, not a Delete — Marten does not version-check deletes, so
    /// a Delete would let two concurrent redemptions of one ceremony_id both mint
    /// a token. A consumed row therefore still exists but must never be usable.
    /// </summary>
    private async Task<bool> CeremonyIsRedeemableAsync(Guid ceremonyId)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var doc = await session.LoadAsync<PasskeyCeremony>(ceremonyId, TestContext.Current.CancellationToken);
        return doc is not null && !doc.IsConsumed && !doc.IsExpired;
    }

    private Task SeedPasskeyClientAsync(string clientId, string? rpId = null) =>
        SeedClientAsync(clientId, [CocoarGrantTypes.Passkey, "refresh_token"], rpId);

    private async Task SeedClientAsync(string clientId, List<string> grantTypes, string? rpId = null)
    {
        var app = await CreateAppAsync($"{clientId}-catalog", clientId);

        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"{clientId}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "email", "profile", "offline_access"],
            AllowedGrantTypes = grantTypes,
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            WebAuthnRpId = rpId,
            AppIds = [new ShortGuid(app.Id).ToString()],
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task<App> CreateAppAsync(string slug, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: displayName, Description: null,
            Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
