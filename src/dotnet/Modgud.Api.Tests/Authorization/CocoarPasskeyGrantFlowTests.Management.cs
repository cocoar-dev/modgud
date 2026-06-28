using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// The cookieless, Bearer-authenticated passkey MANAGEMENT pair
/// (<c>GET /connect/passkey</c> + <c>DELETE /connect/passkey/{id}</c>): a native
/// client or brokering BFF lists and revokes the token subject's own passkeys
/// without a Modgud cookie/session. Covers the NativeGrants gate, the Bearer
/// requirement, strict owner-scoping (a foreign / unknown id is a 404, never a 403),
/// and the end-to-end guarantee that a deleted passkey can no longer satisfy a
/// <c>urn:cocoar:passkey</c> assertion.
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    // ─────────────────────────────── list ─────────────────────────────────────

    [Fact]
    public async Task Manage_List_ReturnsOnlyOwnPasskeys()
    {
        await EnableNativeGrantsAsync();
        var token = await MintAccessTokenAsync();

        var lastUsed = DateTimeOffset.UtcNow.AddMinutes(-3);
        var ownId = await SeedOwnedCredentialAsync(DefaultUser!.Id, "My Phone", lastUsed);
        var foreignUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Other", lastname: "Person", acronym: "OP", email: "other@test.com");
        var foreignId = await SeedOwnedCredentialAsync(foreignUser.Id, "Foreign Phone");

        var resp = await BearerClient(token).GetAsync("/connect/passkey", TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"GET /connect/passkey failed ({(int)resp.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        var ids = json.RootElement.EnumerateArray().Select(e => e.GetProperty("Id").GetString()).ToList();
        Assert.Contains(ownId.ToString(), ids);
        Assert.DoesNotContain(foreignId.ToString(), ids); // never another user's credentials
        Assert.Single(ids); // exactly the one own credential, nothing leaked

        // The DTO is the documented shape { id, displayName, createdAt, lastUsedAt }
        // (serialized PascalCase here, per the API's PropertyNamingPolicy = null).
        var item = json.RootElement.EnumerateArray().Single();
        Assert.Equal("My Phone", item.GetProperty("DisplayName").GetString());
        Assert.True(item.TryGetProperty("CreatedAt", out _));
        Assert.Equal(lastUsed, item.GetProperty("LastUsedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Manage_List_Anonymous_Unauthorized()
    {
        await EnableNativeGrantsAsync();
        var anon = Factory.CreateClient();
        var resp = await anon.GetAsync("/connect/passkey", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Manage_List_NativeGrantsOff_BadRequest()
    {
        // Mint while enabled, then flip the realm flag off: the token stays valid but
        // the feature gate must reject the management call (consistency with the rest
        // of the native surface — the flag is the master switch).
        await EnableNativeGrantsAsync();
        var token = await MintAccessTokenAsync();
        await DisableNativeGrantsAsync();

        var resp = await BearerClient(token).GetAsync("/connect/passkey", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("NativeGrants.Disabled", body);
    }

    // ─────────────────────────────── delete ───────────────────────────────────

    [Fact]
    public async Task Manage_Delete_OwnPasskey_RemovesIt()
    {
        await EnableNativeGrantsAsync();
        var token = await MintAccessTokenAsync();
        var ownId = await SeedOwnedCredentialAsync(DefaultUser!.Id, "My Phone");

        var resp = await BearerClient(token).DeleteAsync($"/connect/passkey/{ownId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Null(await LoadStoredCredentialAsync(ownId));
    }

    [Fact]
    public async Task Manage_Delete_ForeignPasskey_NotFound_AndNotDeleted()
    {
        // Owner-scoped: deleting another user's credential is a 404 (not 403 — no
        // cross-user existence oracle) and must NOT remove it.
        await EnableNativeGrantsAsync();
        var token = await MintAccessTokenAsync();
        var foreignUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Other", lastname: "Person", acronym: "OP", email: "other@test.com");
        var foreignId = await SeedOwnedCredentialAsync(foreignUser.Id, "Foreign Phone");

        var resp = await BearerClient(token).DeleteAsync($"/connect/passkey/{foreignId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.NotNull(await LoadStoredCredentialAsync(foreignId)); // untouched
    }

    [Fact]
    public async Task Manage_Delete_UnknownId_NotFound()
    {
        await EnableNativeGrantsAsync();
        var token = await MintAccessTokenAsync();

        var resp = await BearerClient(token).DeleteAsync($"/connect/passkey/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Manage_Delete_Anonymous_Unauthorized()
    {
        await EnableNativeGrantsAsync();
        var anon = Factory.CreateClient();
        var resp = await anon.DeleteAsync($"/connect/passkey/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Manage_Delete_OwnPasskey_ThenNativeLogin_InvalidGrant()
    {
        // A2 "Done": a deleted passkey is immediately no longer redeemable for the
        // urn:cocoar:passkey grant. Bootstrap a real credential, sign in once to get a
        // token, delete that credential via the Bearer endpoint, then prove the same
        // authenticator can no longer mint a token.
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("native-passkey-app");

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle);

        // Sign in with the seeded credential → access token.
        var (ceremonyId, challenge, rpId) = await BeginAsync();
        var assertion = authenticator.CreateAssertionJson(challenge, rpId, $"https://{rpId}");
        var loginResp = await PostPasskeyAsync("native-passkey-app", ceremonyId, assertion);
        var loginBody = await loginResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(loginResp.IsSuccessStatusCode, $"bootstrap login failed ({(int)loginResp.StatusCode}): {loginBody}");
        var token = JsonDocument.Parse(loginBody).RootElement.GetProperty("access_token").GetString()!;

        // Delete it via the Bearer management endpoint.
        var stored = await LoadCredentialAsync(authenticator.CredentialId);
        Assert.NotNull(stored);
        var delResp = await BearerClient(token).DeleteAsync($"/connect/passkey/{stored!.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // The same authenticator can no longer satisfy a fresh passkey ceremony.
        var (ceremonyId2, challenge2, rpId2) = await BeginAsync();
        var assertion2 = authenticator.CreateAssertionJson(challenge2, rpId2, $"https://{rpId2}");
        var afterResp = await PostPasskeyAsync("native-passkey-app", ceremonyId2, assertion2);

        Assert.False(afterResp.IsSuccessStatusCode);
        var afterBody = await afterResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", afterBody);
    }

    // ─────────────────────────────── helpers ──────────────────────────────────

    private HttpClient BearerClient(string accessToken)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Mints a real Bearer access token for <see cref="IntegrationTestBase.DefaultUser"/>
    /// via the native magic grant — the lightest cookieless mint (seed a challenge doc +
    /// one token POST, no WebAuthn ceremony, no email, no rate-limited begin).</summary>
    private async Task<string> MintAccessTokenAsync()
    {
        await SeedClientAsync("native-mgmt-app", [CocoarGrantTypes.Magic, "refresh_token"]);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SeedMagicLinkAsync(DefaultUser!.Id, token);

        var resp = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Magic,
            ["client_id"] = "native-mgmt-app",
            ["client_secret"] = "native-mgmt-app-secret",
            ["user_id"] = DefaultUser!.Id.ToString(),
            ["magic_token"] = token,
            ["scope"] = "openid email profile offline_access",
        });
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"token mint (magic) failed ({(int)resp.StatusCode}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Seeds a <see cref="MagicLinkChallenge"/> directly (opaque token, SHA-256
    /// at rest) — bypasses the rate-limited request endpoint, exactly as the Phase-1
    /// grant tests do.</summary>
    private async Task SeedMagicLinkAsync(Guid userId, string token)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new MagicLinkChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = MagicLinkChallenge.HashToken(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a passkey credential owned by <paramref name="userId"/> and
    /// returns its addressable <see cref="StoredPasskeyCredential.Id"/>.</summary>
    private async Task<Guid> SeedOwnedCredentialAsync(Guid userId, string displayName, DateTimeOffset? lastUsedAt = null)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.CreateVersion7();
        session.Store(new StoredPasskeyCredential
        {
            Id = id,
            UserId = userId,
            CredentialId = RandomNumberGenerator.GetBytes(32),
            PublicKey = RandomNumberGenerator.GetBytes(64),
            UserHandle = userId.ToByteArray(),
            SignatureCount = 0,
            AttestationType = "none",
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = lastUsedAt,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<StoredPasskeyCredential?> LoadStoredCredentialAsync(Guid id)
    {
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.LoadAsync<StoredPasskeyCredential>(id, TestContext.Current.CancellationToken);
    }

    private async Task DisableNativeGrantsAsync()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = false },
        }, TestContext.Current.CancellationToken);
    }
}
