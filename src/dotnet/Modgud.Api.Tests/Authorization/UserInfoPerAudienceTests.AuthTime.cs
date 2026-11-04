using System.Text;
using System.Text.Json;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// dw63 — user ID tokens carried no <c>auth_time</c>. OpenID Connect Core §2 makes it REQUIRED
/// when the RP sent <c>max_age</c>, and without it an RP cannot check for itself that
/// <c>prompt=login</c> led to a fresh sign-in. It now carries the time of the latest proven
/// sign-in factor and keeps that original time across a refresh.
/// </summary>
public partial class UserInfoPerAudienceTests
{
    [Fact]
    public async Task The_id_token_carries_auth_time_and_a_refresh_keeps_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync($"authtime-{Guid.NewGuid():N}"[..20], "Auth time", permissions: [("doc", "read")]);
        var clientId = "authtime-" + Guid.NewGuid().ToString("N");
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [app.Id], scopes: ["openid", "offline_access"], accessTokenType: AccessTokenType.Reference);
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Auth", lastname: "Time", acronym: "at", email: "auth-time@test.com", password: "TestPass1234");

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        using var tokens = await DriveAuthCodeFlowForTokensAsync(
            username: "at", password: "TestPass1234",
            clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            scope: "openid offline_access", resources: []);
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1;

        var authTime = AuthTimeOf(tokens.RootElement.GetProperty("id_token").GetString()!);
        Assert.NotNull(authTime);
        Assert.InRange(authTime!.Value, before, after);

        // A refresh issues a new ID token for the SAME sign-in: auth_time stays the original.
        await Task.Delay(TimeSpan.FromSeconds(1.2), ct);
        using var refreshed = await PostRefreshTokenAsync(
            tokens.RootElement.GetProperty("refresh_token").GetString()!, clientId, clientSecret, []);
        Assert.True(refreshed.IsSuccessStatusCode, await refreshed.Content.ReadAsStringAsync(ct));
        using var refreshedJson = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync(ct));
        Assert.Equal(authTime, AuthTimeOf(refreshedJson.RootElement.GetProperty("id_token").GetString()!));
    }

    private static long? AuthTimeOf(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return json.RootElement.TryGetProperty("auth_time", out var value) ? value.GetInt64() : null;
    }
}
