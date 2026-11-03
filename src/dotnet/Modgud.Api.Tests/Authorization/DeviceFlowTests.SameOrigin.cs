using System.Net;
using System.Text.Json;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// EG-01 — the approval POST to <c>connect/verify</c> binds the victim's session to a
/// device code by <c>user_code</c> alone. A page on another origin of the same site
/// (a sibling subdomain) gets the session cookie sent along, SameSite or not, so it
/// could approve the attacker's own device code. The decision must come from the
/// IdP's own origin; a caller without browser fetch metadata (non-browser) is unchanged.
/// </summary>
public partial class DeviceFlowTests
{
    [Theory]
    [InlineData("same-site", "http://evil.localhost")]
    [InlineData("cross-site", "https://evil.example")]
    [InlineData(null, "http://evil.localhost")]          // no Sec-Fetch-Site: the Origin decides
    [InlineData(null, "null")]                            // opaque origin (sandboxed frame, data: URL)
    public async Task Approval_from_another_origin_is_refused_and_issues_no_token(string? fetchSite, string origin)
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedDeviceClientAsync();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(clientId);
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");

        using var approve = await SubmitDecisionFromAsync(cookieClient, userCode, fetchSite, origin);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

        using var token = await PollTokenAsync(clientId, deviceCode);
        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        using var doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync(ct));
        Assert.Equal("authorization_pending", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Approval_from_the_idp_origin_still_completes_the_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedDeviceClientAsync();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(clientId);
        var cookieClient = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(cookieClient, userCode);

        using var approve = await SubmitDecisionFromAsync(cookieClient, userCode, "same-origin", "http://localhost");
        Assert.True((int)approve.StatusCode < 400, $"approve failed ({(int)approve.StatusCode})");

        using var token = await PollTokenAsync(clientId, deviceCode);
        Assert.True(token.IsSuccessStatusCode, $"/connect/token failed ({(int)token.StatusCode})");
    }

    private static Task<HttpResponseMessage> SubmitDecisionFromAsync(
        HttpClient cookieClient, string userCode, string? fetchSite, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/verify")
        {
            Content = new FormUrlEncodedContent([new("user_code", userCode), new("decision", "approve")]),
        };
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        request.Headers.Add("Origin", origin);
        return cookieClient.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
