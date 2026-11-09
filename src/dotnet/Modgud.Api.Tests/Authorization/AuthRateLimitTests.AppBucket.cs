using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B8-AJ-03 — on a realm host without an App pin the limits come from the
/// authenticated client's App, so the App bucket must be that App's too. It was
/// "realm" for every client: clients of different Apps drew on one counter.
/// </summary>
public partial class AuthRateLimitTests
{
    [Fact]
    public async Task Clients_of_different_apps_count_against_their_own_app_bucket()
    {
        var ct = TestContext.Current.CancellationToken;
        var appA = await CreateAppAsync("rl-bucket-a");
        var appB = await CreateAppAsync("rl-bucket-b");
        await CreateAppClientAsync("rl-bucket-client-a", appA.Id);
        await CreateAppClientAsync("rl-bucket-client-b", appB.Id);
        await PatchAsync(Policy("native-otp", source: Rule(50, 60), target: Rule(50, 60), client: Rule(50, 60), app: Rule(2, 60)),
            mode: RateLimitEnforcementMode.Enforce);
        var anon = Factory.CreateClient();
        var a = ("rl-bucket-client-a", "rl-bucket-client-a-secret");
        var b = ("rl-bucket-client-b", "rl-bucket-client-b-secret");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, null, "bucket-a1@nowhere.example", basic: a)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, null, "bucket-a2@nowhere.example", basic: a)).StatusCode);

        // App A's budget is spent; App B's is untouched.
        var b1 = await SendAsync(anon, null, "bucket-b1@nowhere.example", basic: b);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, b1.StatusCode);

        var a3 = await SendAsync(anon, null, "bucket-a3@nowhere.example", basic: a);
        Assert.Equal(HttpStatusCode.TooManyRequests, a3.StatusCode);
        Assert.Equal("app", (await a3.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct))!.Dimension);
    }

    private async Task CreateAppClientAsync(string clientId, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(
            ClientDto(clientId, OAuthClientTypes.Confidential, []) with { AppIds = [appId.ToString()] },
            TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
    }
}
