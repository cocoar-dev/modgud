using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Modgud.Client.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Modgud.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins <see cref="ModgudUserInfoEnricher"/>'s preference order (issue #116,
/// Option A): the access token's own embedded <c>resource_access</c> claim
/// wins — <c>/connect/userinfo</c> is called ONLY as a fallback when the
/// validated token carries none.
///
/// <para>Why this is safe: since federation v1.1 the IdP bakes
/// <c>resource_access</c> into every access token at issuance, and
/// <c>/connect/userinfo</c> merely echoes that same block back verbatim
/// (IdP-side <c>UserInfoPerAudienceTests
/// .JwtClient_Bakes_ResourceAccess_Into_AccessToken_And_UserInfo_Echoes</c>
/// pins the echo). Preferring the token claim therefore changes freshness
/// in no way — it only removes a redundant per-request HTTP round-trip for
/// tokens that already carry the claim.</para>
///
/// <para>The JWT-mapped claim shape is reproduced faithfully here (claim
/// type <c>"resource_access"</c>, raw-JSON-text <c>Value</c>,
/// <see cref="JsonClaimValueTypes.Json"/> <c>ValueType</c>) — see the
/// <c>ModgudClaimsTransformation</c> doc remarks for how that shape was
/// established empirically.</para>
/// </summary>
public class UserInfoEnricherTests
{
    private const string Authority = "https://auth.example.com";

    /// <summary>
    /// Counts invocations and, unless a canned responder is supplied,
    /// throws on any call — so an unexpected HTTP attempt fails the test
    /// loudly instead of silently succeeding against a stub.
    /// </summary>
    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (respond is null)
                throw new InvalidOperationException(
                    "Modgud.UserInfoEnricher made an HTTP call the test did not expect.");
            return Task.FromResult(respond(request));
        }
    }

    private static IServiceProvider NewServices(string authority = Authority)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IOptions<ModgudOptions>>(
            Options.Create(new ModgudOptions { Authority = authority, Audience = "aud" }));
        return services.BuildServiceProvider();
    }

    private static TokenValidatedContext NewContext(
        IServiceProvider services, ClaimsPrincipal principal, string? bearerToken = "raw-access-token")
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        if (bearerToken is not null)
            httpContext.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken).ToString();

        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme, typeof(JwtBearerHandler));
        return new TokenValidatedContext(httpContext, scheme, new JwtBearerOptions())
        {
            Principal = principal,
        };
    }

    /// <summary>
    /// Mirrors exactly how ASP.NET Core's JwtBearer (JsonWebTokenHandler)
    /// maps a JSON-object JWT payload property onto the validated
    /// principal: claim type = the payload key verbatim, Value = raw JSON
    /// text, ValueType = <see cref="JsonClaimValueTypes.Json"/>.
    /// </summary>
    private static ClaimsPrincipal PrincipalWithTokenEmbeddedResourceAccess(string rawJson)
    {
        var identity = new ClaimsIdentity(authenticationType: "AuthenticationTypes.Federation");
        identity.AddClaim(new Claim(
            ModgudClaimsTransformation.ResourceAccessClaimType, rawJson,
            JsonClaimValueTypes.Json));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal PrincipalWithoutResourceAccess()
    {
        var identity = new ClaimsIdentity(authenticationType: "AuthenticationTypes.Federation");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "user-1"));
        return new ClaimsPrincipal(identity);
    }

    // Both nested classes mutate the shared static HttpClient seam
    // (ModgudUserInfoEnricher.SharedClient) for the duration of each test.
    // xUnit parallelizes across different test classes by default, so both
    // are pinned to the same collection to force sequential execution
    // against each other and avoid racing on that shared mutable state.
    [Collection(nameof(UserInfoEnricherTests))]
    public class PrefersTokenClaim
    {
        [Fact]
        public async Task Token_embedded_resource_access_skips_userinfo_round_trip()
        {
            const string rawJson = """{"aud":{"roles":["Editor"],"permissions":["policy:write"]}}""";
            var principal = PrincipalWithTokenEmbeddedResourceAccess(rawJson);
            var ctx = NewContext(NewServices(), principal);

            var handler = new StubHttpMessageHandler(); // throws if invoked
            var original = ModgudUserInfoEnricher.SharedClient;
            ModgudUserInfoEnricher.SharedClient = new HttpClient(handler);
            try
            {
                await ModgudUserInfoEnricher.EnrichAsync(ctx);
            }
            finally
            {
                ModgudUserInfoEnricher.SharedClient = original;
            }

            Assert.Equal(0, handler.CallCount);
            // The token's own claim is left untouched — no duplicate added.
            Assert.Single(((ClaimsIdentity)ctx.Principal!.Identity!)
                .FindAll(ModgudClaimsTransformation.ResourceAccessClaimType));
        }

        [Fact]
        public async Task Empty_string_resource_access_claim_is_treated_as_absent()
        {
            // Defence-in-depth: a claim that technically exists but carries
            // no data must NOT short-circuit the fallback — there'd be
            // nothing for the transformation to read.
            var identity = new ClaimsIdentity(authenticationType: "AuthenticationTypes.Federation");
            identity.AddClaim(new Claim(ModgudClaimsTransformation.ResourceAccessClaimType, ""));
            var principal = new ClaimsPrincipal(identity);
            var ctx = NewContext(NewServices(), principal);

            var handler = new StubHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"resource_access":{"aud":{"roles":[]}}}"""),
            });
            var original = ModgudUserInfoEnricher.SharedClient;
            ModgudUserInfoEnricher.SharedClient = new HttpClient(handler);
            try
            {
                await ModgudUserInfoEnricher.EnrichAsync(ctx);
            }
            finally
            {
                ModgudUserInfoEnricher.SharedClient = original;
            }

            Assert.Equal(1, handler.CallCount);
        }
    }

    [Collection(nameof(UserInfoEnricherTests))]
    public class FallsBackToUserInfo
    {
        [Fact]
        public async Task No_token_claim_fetches_userinfo_and_merges_resource_access()
        {
            var principal = PrincipalWithoutResourceAccess();
            var ctx = NewContext(NewServices(), principal);

            const string resourceAccessJson = """{"aud":{"roles":["Viewer"],"permissions":["policy:read"]}}""";
            var handler = new StubHttpMessageHandler(req =>
            {
                Assert.Equal($"{Authority}/connect/userinfo", req.RequestUri!.ToString());
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("raw-access-token", req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"sub":"u1","resource_access":{{resourceAccessJson}}}"""),
                };
            });
            var original = ModgudUserInfoEnricher.SharedClient;
            ModgudUserInfoEnricher.SharedClient = new HttpClient(handler);
            try
            {
                await ModgudUserInfoEnricher.EnrichAsync(ctx);
            }
            finally
            {
                ModgudUserInfoEnricher.SharedClient = original;
            }

            Assert.Equal(1, handler.CallCount);
            var claim = ((ClaimsIdentity)ctx.Principal!.Identity!)
                .FindFirst(ModgudClaimsTransformation.ResourceAccessClaimType);
            Assert.NotNull(claim);
            Assert.Equal(resourceAccessJson, claim!.Value);
        }

        [Fact]
        public async Task No_token_claim_and_no_bearer_header_makes_no_http_call()
        {
            var principal = PrincipalWithoutResourceAccess();
            var ctx = NewContext(NewServices(), principal, bearerToken: null);

            var handler = new StubHttpMessageHandler(); // throws if invoked
            var original = ModgudUserInfoEnricher.SharedClient;
            ModgudUserInfoEnricher.SharedClient = new HttpClient(handler);
            try
            {
                await ModgudUserInfoEnricher.EnrichAsync(ctx);
            }
            finally
            {
                ModgudUserInfoEnricher.SharedClient = original;
            }

            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task No_token_claim_transport_failure_fails_open()
        {
            var principal = PrincipalWithoutResourceAccess();
            var ctx = NewContext(NewServices(), principal);

            var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("boom"));
            var original = ModgudUserInfoEnricher.SharedClient;
            ModgudUserInfoEnricher.SharedClient = new HttpClient(handler);
            try
            {
                // Must not throw — a transient IdP outage must not 500 the API.
                await ModgudUserInfoEnricher.EnrichAsync(ctx);
            }
            finally
            {
                ModgudUserInfoEnricher.SharedClient = original;
            }

            Assert.Null(((ClaimsIdentity)ctx.Principal!.Identity!)
                .FindFirst(ModgudClaimsTransformation.ResourceAccessClaimType));
        }

        [Fact]
        public async Task No_token_claim_non_success_status_fails_open()
        {
            var principal = PrincipalWithoutResourceAccess();
            var ctx = NewContext(NewServices(), principal);

            var handler = new StubHttpMessageHandler(
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var original = ModgudUserInfoEnricher.SharedClient;
            ModgudUserInfoEnricher.SharedClient = new HttpClient(handler);
            try
            {
                await ModgudUserInfoEnricher.EnrichAsync(ctx);
            }
            finally
            {
                ModgudUserInfoEnricher.SharedClient = original;
            }

            Assert.Equal(1, handler.CallCount);
            Assert.Null(((ClaimsIdentity)ctx.Principal!.Identity!)
                .FindFirst(ModgudClaimsTransformation.ResourceAccessClaimType));
        }
    }
}
