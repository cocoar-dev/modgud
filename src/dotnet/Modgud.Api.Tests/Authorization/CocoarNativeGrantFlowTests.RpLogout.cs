using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Authentication.Sessions;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// EG-06 — an RP-initiated logout revokes the (subject, client) tokens and
/// authorizations; the native client sessions built on them must end with them,
/// instead of staying listed until a later refresh fails.
/// </summary>
public partial class CocoarNativeGrantFlowTests
{
    [Fact]
    public async Task Rp_logout_ends_the_native_client_session_of_that_client()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-logout-app");
        var code = await IssueNativeOtpViaServiceAsync(DefaultUser!.Id);

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-logout-app",
            ["client_secret"] = "native-logout-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["scope"] = "openid offline_access",
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        var idToken = body.GetProperty("id_token").GetString()!;
        Assert.Single((await SessionsAsync()).ClientSessions, x => x.ClientId == "native-logout-app");

        // A cookieless client (the native app's own browser tab), so the default
        // user's cookie session is not the one signed out.
        var logout = await Factory.CreateClient().GetAsync(
            $"/connect/logout?id_token_hint={Uri.EscapeDataString(idToken)}", ct);
        Assert.True((int)logout.StatusCode < 400, $"logout failed ({(int)logout.StatusCode})");

        Assert.DoesNotContain((await SessionsAsync()).ClientSessions, x => x.ClientId == "native-logout-app");
    }

    private async Task<SessionListDto> SessionsAsync() =>
        (await Client.GetFromJsonAsync<SessionListDto>("/api/auth/sessions", JsonOptions, TestContext.Current.CancellationToken))!;
}
