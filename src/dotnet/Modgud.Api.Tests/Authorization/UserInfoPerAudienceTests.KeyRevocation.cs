using System.Net;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// FK-01 — a rotation only retired the previous signing key: it stayed in the verification set
/// and the JWKS for the 30-day overlap, and nothing could take a key out of trust earlier.
/// Rotating after a key compromise therefore left the compromised key trusted for 30 days. A
/// rotation can now revoke the previous key, and retired keys can be revoked on their own; a
/// revoked key verifies nothing from that moment on. Driven through the real flow: a reference
/// access token's stored payload is a realm-signed JWT that <c>/connect/userinfo</c> verifies.
/// </summary>
public partial class UserInfoPerAudienceTests
{
    [Fact]
    public async Task A_token_signed_with_the_previous_key_still_works_after_a_plain_rotation()
    {
        var token = await IssueReferenceTokenAsync("kr1");

        await RotateAsync(query: "");

        Assert.Equal(HttpStatusCode.OK, await UserInfoStatusAsync(token));
    }

    [Fact]
    public async Task Rotating_with_revocation_invalidates_tokens_signed_with_the_previous_key()
    {
        var token = await IssueReferenceTokenAsync("kr2");
        Assert.Equal(HttpStatusCode.OK, await UserInfoStatusAsync(token));

        await RotateAsync(query: "?revokePrevious=true");

        Assert.Equal(HttpStatusCode.Unauthorized, await UserInfoStatusAsync(token));
        // A token issued after the emergency rotation is signed with the new key and works.
        Assert.Equal(HttpStatusCode.OK, await UserInfoStatusAsync(await IssueReferenceTokenAsync("kr3")));
    }

    [Fact]
    public async Task Revoking_retired_keys_invalidates_tokens_signed_before_an_earlier_rotation()
    {
        var token = await IssueReferenceTokenAsync("kr4");
        // FK-03 — an ordinary rotation is scheduled now; this case needs the key retired at once.
        await RotateAsync(query: "?immediate=true");
        Assert.Equal(HttpStatusCode.OK, await UserInfoStatusAsync(token));

        var revoke = await Client.PostAsync("/api/admin/realm-settings/signing-keys/revoke-retired", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await UserInfoStatusAsync(token));
    }

    private async Task<string> IssueReferenceTokenAsync(string userName)
    {
        var app = await CreateAppAsync($"keyrev-{Guid.NewGuid():N}"[..20], "Key revocation", permissions: [("doc", "read")]);
        var clientId = "keyrev-" + Guid.NewGuid().ToString("N");
        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        const string redirectUri = "http://localhost/test-callback";
        await CreateOAuthClientAsync(clientId: clientId, clientSecret: clientSecret, redirectUri: redirectUri,
            appIds: [app.Id], scopes: ["openid"], accessTokenType: AccessTokenType.Reference);
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Key", lastname: "Revocation", acronym: userName, email: $"{userName}@test.com", password: "TestPass1234");

        return await DriveAuthCodeFlowAsync(
            username: userName, password: "TestPass1234",
            clientId: clientId, clientSecret: clientSecret,
            redirectUri: redirectUri, scope: "openid", resources: []);
    }

    private async Task RotateAsync(string query)
    {
        var resp = await Client.PostAsync($"/api/admin/realm-settings/rotate-signing-key{query}", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<HttpStatusCode> UserInfoStatusAsync(string accessToken)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return (await client.GetAsync("/connect/userinfo", TestContext.Current.CancellationToken)).StatusCode;
    }
}
