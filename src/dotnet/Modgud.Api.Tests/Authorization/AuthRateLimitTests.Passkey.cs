using System.Net;
using System.Net.Http.Json;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B8-P3 — the anonymous web passkey login-options endpoint persists a new ceremony on
/// every call but had no auth rate-limit gate, unlike the native passkey begin and the
/// native enrollment. It now shares their <c>passkey-begin</c> policy.
/// </summary>
public partial class AuthRateLimitTests
{
    private const string WebPasskeyOptions = "/api/account/passkey/login-options";

    [Fact]
    public async Task Web_passkey_login_options_are_limited_by_the_passkey_begin_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchAsync(Policy("passkey-begin", source: Rule(2, 5)), mode: RateLimitEnforcementMode.Enforce);
        var anon = Factory.CreateClient();
        var before = await CeremonyCountAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendWebPasskeyOptionsAsync(anon, "web-passkey")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWebPasskeyOptionsAsync(anon, "web-passkey")).StatusCode);

        var rejected = await SendWebPasskeyOptionsAsync(anon, "web-passkey");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct);
        Assert.Equal("passkey-begin", body!.Policy);
        Assert.Equal("source", body.Dimension);

        // The rejected call never reached the handler: no third ceremony document.
        Assert.Equal(before + 2, await CeremonyCountAsync());

        // Another source is not affected.
        Assert.Equal(HttpStatusCode.OK, (await SendWebPasskeyOptionsAsync(anon, "web-passkey-other")).StatusCode);
    }

    private Task<HttpResponseMessage> SendWebPasskeyOptionsAsync(HttpClient client, string budget)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, WebPasskeyOptions) { Content = JsonContent.Create(new { }) };
        req.Headers.Add("X-Test-RateLimit", $"auth-rl-{budget}");
        return client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private async Task<int> CeremonyCountAsync()
    {
        await using var session = GetTenantedDocumentSession();
        return await session.Query<PasskeyCeremony>().CountAsync(TestContext.Current.CancellationToken);
    }
}
