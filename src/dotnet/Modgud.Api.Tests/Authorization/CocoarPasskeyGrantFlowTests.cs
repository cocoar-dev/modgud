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
public class CocoarPasskeyGrantFlowTests : IntegrationTestBase
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
        Assert.True(await CeremonyExistsAsync(ceremonyId));
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
        Assert.False(await CeremonyExistsAsync(ceremonyId));
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
        Assert.False(await CeremonyExistsAsync(ceremonyId));
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
        Assert.False(await CeremonyExistsAsync(ceremonyId));
    }

    // ─────────────────────────────── helpers ──────────────────────────────────

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
            ["scope"] = "openid",
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

    private async Task SeedCredentialAsync(byte[] credentialId)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new StoredPasskeyCredential
        {
            Id = Guid.NewGuid(),
            UserId = DefaultUser!.Id,
            CredentialId = credentialId,
            PublicKey = RandomNumberGenerator.GetBytes(64),
            UserHandle = DefaultUser.Id.ToByteArray(),
            SignatureCount = 0,
            AttestationType = "none",
            DisplayName = "Test passkey",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<bool> CeremonyExistsAsync(Guid ceremonyId)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var doc = await session.LoadAsync<PasskeyCeremony>(ceremonyId, TestContext.Current.CancellationToken);
        return doc is not null;
    }

    private Task SeedPasskeyClientAsync(string clientId) =>
        SeedClientAsync(clientId, [CocoarGrantTypes.Passkey, "refresh_token"]);

    private async Task SeedClientAsync(string clientId, List<string> grantTypes)
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
