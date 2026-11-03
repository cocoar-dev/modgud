using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Identity;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.RateLimiting;
using RealmSettingsDocument = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

// A bounded hypothesis probe, not a reproduction of the original sporadic 500.
// Control and reset variant use the real singleton store and request pipeline.
public sealed class OtpFailurePathHypothesisTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Warm_store_and_native_web_requests_remain_usable_across_reset(bool reset)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Factory.Services.GetRequiredService<IRateLimitStore>();
        var scope = new RateLimitScope("system");
        var prefix = $"hypothesis|{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var fixedRule = RateLimitRule.Fixed(10, 60);
        var bucketRule = RateLimitRule.Bucket(10, 60, 10);
        Assert.True((await store.HitAsync(scope, prefix + "|fixed", fixedRule, now, ct)).Allowed);
        Assert.True((await store.HitAsync(scope, prefix + "|bucket", bucketRule, now, ct)).Allowed);

        if (reset)
            await Factory.ResetMartenDataAsync();

        Assert.Same(store, Factory.Services.GetRequiredService<IRateLimitStore>());
        Assert.True((await store.HitAsync(scope, prefix + "|fixed", fixedRule, now, ct)).Allowed);
        Assert.True((await store.HitAsync(scope, prefix + "|bucket", bucketRule, now, ct)).Allowed);

        var email = $"hypothesis-{Guid.NewGuid():N}@example.test";
        var user = await Factory.CreateTestUserWithIdentityAsync(
            acronym: "probe", email: email, isRealmAdmin: false);
        using (var settings = GetTenantedDocumentSession())
        {
            settings.Store(new RealmSettingsDocument
            {
                NativeGrants = new NativeGrantSettings { Enabled = true },
                SignIn = SignInPolicy.Defaults with { EmailCode = true },
            });
            await settings.SaveChangesAsync(ct);
        }

        using (var services = Factory.Services.CreateScope())
        {
            var issued = await services.ServiceProvider.GetRequiredService<IEmailOtpService>()
                .RequestNativeOtpAsync(user.Id, ct);
            Assert.False(issued.IsError);
        }
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo(email);
        Assert.True(mail is not null);
        var match = Regex.Match(mail!.HtmlBody, @"\b(\d{6})\b");
        Assert.True(match.Success);
        var code = match.Groups[1].Value;
        var wrongCode = (code[0] == '0' ? "1" : "0") + code[1..];

        using var client = Factory.CreateDefaultClient(new CookieContainerHandler());
        using (var invalid = await client.PostAsJsonAsync("/api/account/passwordless-otp/login",
                   new { Email = email, Code = wrongCode }, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        // Submit both gates together, without forcing a reset to race a live request:
        // the real fixture awaits reset before dispatching any test requests.
        var native = client.PostAsJsonAsync("/api/account/native/otp/request", new { Email = "" }, ct);
        var web = client.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = email, Code = code }, ct);
        var responses = await Task.WhenAll(native, web);
        using var nativeResponse = responses[0];
        using var webResponse = responses[1];
        Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, webResponse.StatusCode);
        Assert.False(nativeResponse.Headers.Contains(TestFailureCorrelation.ResponseHeader));
        Assert.False(webResponse.Headers.Contains(TestFailureCorrelation.ResponseHeader));
        using var me = await client.GetAsync("/api/account/me", ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }
}
