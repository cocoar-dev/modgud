using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Pins the per-IP SMTP rate limit on the native passwordless OTP-request
/// endpoint (<c>native-otp</c> policy, 5/hour). This is the ONE place the 429
/// boundary is asserted end-to-end; every other test may hit the endpoint
/// freely because the Testing key selector gives each request its own partition
/// unless it opts into a shared budget via the <c>X-Test-RateLimit</c> header
/// (see EmailLimiterPartitionKey in Program.cs). Replaces the old implicit
/// "only one test may drive this endpoint live" contract — a full-suite
/// shared-budget footgun that surfaced as a displaced 429 flake.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeOtpRateLimitTests : IntegrationTestBase
{
    public NativeOtpRateLimitTests(SharedPostgresFixture fixture) : base(fixture) { }

    // The X-Test-RateLimit header makes these requests share ONE native-otp
    // budget (Testing key selector); a distinct value isolates this test from
    // any other so the count is deterministic.
    private const string SharedBudget = "native-otp-boundary-test";

    [Fact]
    public async Task NativeOtpRequest_ExceedingPerIpLimit_Returns429()
    {
        // The limiter runs before the endpoint handler, so the result is
        // independent of the native-grants flag / email existence: the first
        // PermitLimit (5) requests pass the limiter (200, uniform body), the
        // 6th is rejected with 429.
        const int permitLimit = 5;
        var anon = Factory.CreateClient();

        for (var i = 1; i <= permitLimit; i++)
        {
            var ok = await SendAsync(anon, $"probe{i}@nowhere.example");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var rejected = await SendAsync(anon, "probe-over@nowhere.example");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
        {
            Content = JsonContent.Create(new { Email = email }),
        };
        req.Headers.Add("X-Test-RateLimit", SharedBudget);
        return client.SendAsync(req, TestContext.Current.CancellationToken);
    }
}
