using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.Applications;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Native Android passkeys: Credential Manager signs <c>android:apk-key-hash:&lt;hash&gt;</c>
/// as the origin. It verifies only when the App the ceremony's client belongs to lists that
/// exact origin for the ceremony's RP ID — including a brokered ceremony, where the App's
/// web client redeems what its Android app produced.
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    private const string AndroidRpId = "and.localhost";
    // SHA-256 fingerprint 37:12:A2:19:… in its origin form.
    private const string ListedAndroidOrigin = "android:apk-key-hash:NxKiGexuyBGDGdVtp4Is8MhaKBquHQNHiw-IG3hPtiw";
    private const string UnlistedAndroidOrigin = "android:apk-key-hash:8MCA0HyEcdTiOZIgwjfDzC-NQV8gVghlFDHQtjNVIfY";

    [Fact]
    public async Task NativeEnroll_from_a_listed_Android_app_origin_succeeds()
    {
        await EnableNativeGrantsAsync();
        var appId = await SeedAndroidAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("and-enroll", rpId: AndroidRpId, appId: appId);

        var bearer = await BearerForAsync("and-enroll");
        var (status, body, credentialId) = await EnrollAsync(bearer, ListedAndroidOrigin);

        Assert.True(status is >= 200 and < 300, $"enroll from a listed Android origin failed ({status}): {body}");
        var stored = await LoadCredentialAsync(credentialId);
        Assert.NotNull(stored);
        Assert.Equal(AndroidRpId, stored!.RpId);
    }

    [Fact]
    public async Task NativeEnroll_from_an_unlisted_Android_app_origin_is_rejected()
    {
        await EnableNativeGrantsAsync();
        var appId = await SeedAndroidAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("and-unlisted", rpId: AndroidRpId, appId: appId);

        var bearer = await BearerForAsync("and-unlisted");
        var (status, body, credentialId) = await EnrollAsync(bearer, UnlistedAndroidOrigin);

        Assert.Equal(400, status);
        Assert.Contains("Passkey enrollment failed", body);
        Assert.Null(await LoadCredentialAsync(credentialId));
    }

    [Fact]
    public async Task NativeLogin_from_a_listed_Android_app_origin_mints_a_token()
    {
        await EnableNativeGrantsAsync();
        var appId = await SeedAndroidAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("and-login", rpId: AndroidRpId, appId: appId);

        var response = await AndroidLoginAsync("and-login", ListedAndroidOrigin);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"login from a listed Android origin failed ({(int)response.StatusCode}): {body}");
        Assert.True(JsonDocument.Parse(body).RootElement.TryGetProperty("access_token", out _));
    }

    [Fact]
    public async Task NativeLogin_brokered_by_the_Apps_web_client_accepts_its_Android_apps_origin()
    {
        // amZettel's backend begins and redeems the ceremony with its web client; the
        // assertion comes from the Android app. Both clients belong to the same App.
        await EnableNativeGrantsAsync();
        var appId = await SeedAndroidAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("and-android", rpId: AndroidRpId, appId: appId);
        await SeedPasskeyClientAsync("and-web", rpId: AndroidRpId, appId: appId);

        var response = await AndroidLoginAsync("and-web", ListedAndroidOrigin);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"brokered login failed ({(int)response.StatusCode}): {body}");
    }

    [Theory]
    [InlineData(UnlistedAndroidOrigin, "listed")]   // a hash the App does not list
    [InlineData(ListedAndroidOrigin, "other-app")]  // listed — but by another App
    [InlineData(ListedAndroidOrigin, "no-rp-id")]   // the App lists it, but has no passkey RP ID
    public async Task NativeLogin_from_an_Android_origin_the_clients_App_does_not_allow_is_rejected(
        string origin, string setup)
    {
        await EnableNativeGrantsAsync();
        var clientId = $"and-{setup}";
        Guid appId;
        switch (setup)
        {
            case "other-app":
                await SeedAndroidAppAsync([ListedAndroidOrigin]);
                appId = await SeedAndroidAppAsync(null);
                break;
            case "no-rp-id":
                appId = await SeedAndroidAppAsync([ListedAndroidOrigin], passkeyRpId: null);
                break;
            default:
                appId = await SeedAndroidAppAsync([ListedAndroidOrigin]);
                break;
        }
        await SeedPasskeyClientAsync(clientId, rpId: AndroidRpId, appId: appId);

        var response = await AndroidLoginAsync(clientId, origin);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(response.IsSuccessStatusCode, $"an Android origin the App does not allow was accepted: {body}");
        Assert.Contains("invalid_grant", body);
    }

    [Fact]
    public async Task An_https_origin_still_verifies_for_an_App_with_Android_apps()
    {
        await EnableNativeGrantsAsync();
        var appId = await SeedAndroidAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("and-https", rpId: AndroidRpId, appId: appId);

        var response = await AndroidLoginAsync("and-https", $"https://{AndroidRpId}");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"https login failed ({(int)response.StatusCode}): {body}");
    }

    private async Task<HttpResponseMessage> AndroidLoginAsync(string clientId, string origin)
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(authenticator.CredentialId, authenticator.CosePublicKey(),
            authenticator.UserHandle, rpId: AndroidRpId);
        var (ceremonyId, challenge, rpId) = await BeginAsync(clientId);
        Assert.Equal(AndroidRpId, rpId);
        var assertion = authenticator.CreateAssertionJson(challenge, rpId, origin);
        return await PostPasskeyAsync(clientId, ceremonyId, assertion);
    }

    private async Task<HttpClient> BearerForAsync(string clientId)
    {
        var response = await AndroidLoginAsync(clientId, $"https://{AndroidRpId}");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"bootstrap login failed ({(int)response.StatusCode}): {body}");
        var bearer = Factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization =
            new("Bearer", JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString());
        return bearer;
    }

    private async Task<(int Status, string Body, byte[] CredentialId)> EnrollAsync(HttpClient bearer, string origin)
    {
        var ct = TestContext.Current.CancellationToken;
        var beginResp = await bearer.PostAsync("/connect/passkey/enroll/begin", content: null, ct);
        var beginBody = await beginResp.Content.ReadAsStringAsync(ct);
        Assert.True(beginResp.IsSuccessStatusCode, $"enroll/begin failed ({(int)beginResp.StatusCode}): {beginBody}");
        using var beginJson = JsonDocument.Parse(beginBody);
        var ceremonyId = beginJson.RootElement.GetProperty("ceremonyId").GetString()!;
        var options = beginJson.RootElement.GetProperty("options");
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        Assert.Equal(AndroidRpId, rpId);

        using var enrolling = new SoftwareWebAuthnAuthenticator(Encoding.UTF8.GetBytes(DefaultUser!.Id.ToString()));
        var attestation = enrolling.CreateAttestationJson(challenge, rpId, origin);
        var response = await bearer.PostAsync("/connect/passkey/enroll",
            new StringContent($"{{\"ceremonyId\":\"{ceremonyId}\",\"attestation\":{attestation}}}",
                Encoding.UTF8, "application/json"), ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), enrolling.CredentialId);
    }

    private async Task<Guid> SeedAndroidAppAsync(string[]? androidOrigins, string? passkeyRpId = AndroidRpId)
    {
        var app = await CreateAppAsync($"android-{Guid.NewGuid():N}"[..20], "Android test app");
        using var scope = NewSystemTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApplicationSettings
        {
            Id = app.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            SignIn = new ApplicationSignInOverrides
            {
                PasskeyRpId = passkeyRpId,
                PasskeyAndroidOrigins = androidOrigins,
            },
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return app.Id;
    }
}
