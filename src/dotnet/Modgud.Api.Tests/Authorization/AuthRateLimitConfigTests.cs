using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Verifies the per-realm configurable auth rate-limit ceilings actually drive the
/// live ASP.NET limiter (native-otp policy as the representative case). A realm that
/// lowers the limit gets throttled sooner; one that raises it past the shipped
/// default (5/h) is allowed more. The limit is resolved per request from the realm's
/// AuthRateLimits via AuthRateLimitResolutionMiddleware (TTL=0 in Testing, so a patch
/// takes effect immediately). Requests share one budget via the X-Test-RateLimit
/// header — distinct per test so the limit value baked into the partition key keeps
/// the two tests isolated.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthRateLimitConfigTests : IntegrationTestBase
{
    public AuthRateLimitConfigTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task LoweredNativeOtpLimit_ThrottlesSooner()
    {
        await SetNativeOtpLimitAsync(permitLimit: 2, windowMinutes: 60);
        var anon = Factory.CreateClient();

        // With the ceiling lowered to 2, the first two pass the limiter and the
        // third is throttled — proving the realm override drives the live limiter.
        for (var i = 1; i <= 2; i++)
        {
            var passed = await SendAsync(anon, "lowered", $"probe{i}@nowhere.example");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, passed.StatusCode);
        }

        var rejected = await SendAsync(anon, "lowered", "probe-over@nowhere.example");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task RaisedNativeOtpLimit_AllowsPastShippedDefault()
    {
        // Raise the ceiling well past the shipped default of 5/h.
        await SetNativeOtpLimitAsync(permitLimit: 20, windowMinutes: 60);
        var anon = Factory.CreateClient();

        // Six requests — one more than the old hardcoded default — must all pass the
        // limiter. Under the un-raised 5/h ceiling the sixth would have been 429.
        for (var i = 1; i <= 6; i++)
        {
            var passed = await SendAsync(anon, "raised", $"probe{i}@nowhere.example");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, passed.StatusCode);
        }
    }

    private async Task SetNativeOtpLimitAsync(int permitLimit, int windowMinutes)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var result = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            AuthRateLimits = new UpdateAuthRateLimitsDto
            {
                NativeOtp = new RateLimitRuleDto { PermitLimit = permitLimit, WindowMinutes = windowMinutes },
            },
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string budget, string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
        {
            Content = JsonContent.Create(new { Email = email }),
        };
        // Shared budget so the requests partition together; distinct per test.
        req.Headers.Add("X-Test-RateLimit", $"auth-rl-config-{budget}");
        return client.SendAsync(req, TestContext.Current.CancellationToken);
    }
}
