using System.Security.Claims;
using Cocoar.Auth.Client.AspNetCore;
using Cocoar.Auth.Client.AspNetCore.Distribution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins the resource-server-side claims-transformation: it calls the
/// distribution API once per request, then surfaces the response on the
/// principal as flat ClaimTypes.Role / "permission" / "group" claims so
/// downstream gates work without per-endpoint plumbing.
/// </summary>
public class CocoarAuthClaimsTransformationTests
{
    private static CocoarAuthOptions DefaultOptions() => new()
    {
        AppSlug = "cocoar-policy",
        IdpBaseUrl = "https://auth.cocoar.dev",
        ResourceServerId = "policy-api",
        ResourceServerSecret = "test-secret",
    };

    private static (CocoarAuthClaimsTransformation Subject, FakeDistributionClient Client) NewSubject(
        MePermissionsResponse response,
        string? bearerToken = "test-bearer-token",
        CocoarAuthOptions? options = null)
    {
        var opts = Options.Create(options ?? DefaultOptions());
        var http = new HttpContextAccessor
        {
            HttpContext = bearerToken is null
                ? new DefaultHttpContext()
                : NewContextWithAuthHeader($"Bearer {bearerToken}"),
        };
        var client = new FakeDistributionClient(response);
        var cache = new PermissionsCache(new MemoryCache(new MemoryCacheOptions()), opts);
        var subject = new CocoarAuthClaimsTransformation(
            http, client, cache, opts, NullLogger<CocoarAuthClaimsTransformation>.Instance);
        return (subject, client);
    }

    private static HttpContext NewContextWithAuthHeader(string headerValue)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = headerValue;
        return ctx;
    }

    private static ClaimsPrincipal NewAuthenticatedPrincipal(string sub = "alice", string jti = "tok-1", string authType = "test")
    {
        var identity = new ClaimsIdentity(authType);
        identity.AddClaim(new Claim(Claims.Subject, sub));
        identity.AddClaim(new Claim(Claims.JwtId, jti));
        return new ClaimsPrincipal(identity);
    }

    private sealed class FakeDistributionClient : IDistributionClient
    {
        private readonly MePermissionsResponse _response;
        public int CallCount { get; private set; }
        public string? LastBearerToken { get; private set; }
        public Func<MePermissionsResponse>? OnCall { get; set; }

        public FakeDistributionClient(MePermissionsResponse response) => _response = response;

        public Task<MePermissionsResponse> GetMePermissionsAsync(string userBearerToken, CancellationToken ct = default)
        {
            CallCount++;
            LastBearerToken = userBearerToken;
            return Task.FromResult(OnCall?.Invoke() ?? _response);
        }
    }

    public class Roles
    {
        [Fact]
        public async Task Distribution_response_roles_become_ClaimTypes_Role()
        {
            var response = new MePermissionsResponse(
                UserId: "alice", AppSlug: "cocoar-policy",
                Permissions: [],
                Groups: [],
                Roles: [new RoleRef("r-1", "Editor"), new RoleRef("r-2", "Viewer")]);
            var (subject, _) = NewSubject(response);

            var transformed = await subject.TransformAsync(NewAuthenticatedPrincipal());

            var roles = transformed.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
            Assert.Contains("Editor", roles);
            Assert.Contains("Viewer", roles);
        }

        [Fact]
        public async Task Idempotent_double_run_does_not_duplicate_roles_and_does_not_recall_distribution()
        {
            // ClaimsTransformation runs more than once per request when the
            // principal is rebuilt. Duplicate role claims would bloat policy
            // checks; a double distribution call would double the network
            // cost. The marker-claim guard prevents both.
            var response = new MePermissionsResponse(
                UserId: "alice", AppSlug: "cocoar-policy",
                Permissions: [], Groups: [],
                Roles: [new RoleRef("r-1", "Editor")]);
            var (subject, client) = NewSubject(response);
            var principal = NewAuthenticatedPrincipal();

            await subject.TransformAsync(principal);
            await subject.TransformAsync(principal);

            Assert.Single(principal.FindAll(ClaimTypes.Role));
            Assert.Equal(1, client.CallCount);
        }
    }

    public class Permissions
    {
        [Fact]
        public async Task Distribution_response_permissions_become_permission_claims()
        {
            var response = new MePermissionsResponse(
                UserId: "alice", AppSlug: "cocoar-policy",
                Permissions: ["policy:read", "policy:write"],
                Groups: [], Roles: []);
            var (subject, _) = NewSubject(response);

            var transformed = await subject.TransformAsync(NewAuthenticatedPrincipal());

            var permissions = transformed
                .FindAll(CocoarAuthClaimsTransformation.PermissionClaimType)
                .Select(c => c.Value)
                .ToList();
            Assert.Contains("policy:read", permissions);
            Assert.Contains("policy:write", permissions);
        }
    }

    public class Groups
    {
        [Fact]
        public async Task Distribution_response_groups_become_group_claims()
        {
            var response = new MePermissionsResponse(
                UserId: "alice", AppSlug: "cocoar-policy",
                Permissions: [], Roles: [],
                Groups: [new GroupRef("g-1", "DevOps"), new GroupRef("g-2", "Mitarbeiter")]);
            var (subject, _) = NewSubject(response);

            var transformed = await subject.TransformAsync(NewAuthenticatedPrincipal());

            var groups = transformed
                .FindAll(CocoarAuthClaimsTransformation.GroupClaimType)
                .Select(c => c.Value)
                .ToList();
            Assert.Equal(2, groups.Count);
            Assert.Contains("DevOps", groups);
            Assert.Contains("Mitarbeiter", groups);
        }
    }

    public class ShortCircuits
    {
        [Fact]
        public async Task Anonymous_principal_skips_distribution_call()
        {
            var response = new MePermissionsResponse(
                "alice", "cocoar-policy", [], [], []);
            var (subject, client) = NewSubject(response);
            var anon = new ClaimsPrincipal(new ClaimsIdentity());

            await subject.TransformAsync(anon);

            Assert.Equal(0, client.CallCount);
        }

        [Fact]
        public async Task Missing_sub_or_jti_skips_distribution_call()
        {
            // sub + jti drive the cache key. Without them the lib bails
            // gracefully rather than caching the wrong thing — admins
            // see the 403 from the endpoint filter and investigate.
            var response = new MePermissionsResponse(
                "alice", "cocoar-policy", ["policy:read"], [], []);
            var (subject, client) = NewSubject(response);

            // Authenticated identity but no sub/jti claims.
            var identity = new ClaimsIdentity("test");
            var principal = new ClaimsPrincipal(identity);
            await subject.TransformAsync(principal);

            Assert.Equal(0, client.CallCount);
            Assert.Empty(principal.FindAll(CocoarAuthClaimsTransformation.PermissionClaimType));
        }

        [Fact]
        public async Task Missing_bearer_token_on_request_skips_distribution_call()
        {
            // Without a bearer token forwarded from the incoming request,
            // there's nothing to authenticate the user against the
            // distribution API.
            var response = new MePermissionsResponse(
                "alice", "cocoar-policy", ["policy:read"], [], []);
            var (subject, client) = NewSubject(response, bearerToken: null);

            await subject.TransformAsync(NewAuthenticatedPrincipal());

            Assert.Equal(0, client.CallCount);
        }

        [Fact]
        public async Task Distribution_failure_is_swallowed_and_request_continues()
        {
            // A networking blip on the IdP must NOT 500 the request — the
            // endpoint filter / [Authorize] gate will return 403 if the
            // claims aren't there, which is the security-positive default.
            var response = new MePermissionsResponse(
                "alice", "cocoar-policy", [], [], []);
            var (subject, client) = NewSubject(response);
            client.OnCall = () => throw new HttpRequestException("simulated network failure");
            var principal = NewAuthenticatedPrincipal();

            // Doesn't throw.
            await subject.TransformAsync(principal);

            Assert.Empty(principal.FindAll(ClaimTypes.Role));
            Assert.Empty(principal.FindAll(CocoarAuthClaimsTransformation.PermissionClaimType));
        }
    }

    public class Configuration
    {
        [Fact]
        public void Constructor_throws_when_required_options_are_missing()
        {
            // Fail fast at host build, not silently at request time.
            var http = new HttpContextAccessor();
            var cache = new PermissionsCache(
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new CocoarAuthOptions { AppSlug = "x", IdpBaseUrl = "x", ResourceServerId = "x", ResourceServerSecret = "x" }));
            var distribution = new FakeDistributionClient(new MePermissionsResponse("a", "b", [], [], []));

            Assert.Throws<InvalidOperationException>(() =>
                new CocoarAuthClaimsTransformation(http, distribution, cache,
                    Options.Create(new CocoarAuthOptions { AppSlug = "" }),
                    NullLogger<CocoarAuthClaimsTransformation>.Instance));

            Assert.Throws<InvalidOperationException>(() =>
                new CocoarAuthClaimsTransformation(http, distribution, cache,
                    Options.Create(new CocoarAuthOptions { AppSlug = "x", IdpBaseUrl = "" }),
                    NullLogger<CocoarAuthClaimsTransformation>.Instance));

            Assert.Throws<InvalidOperationException>(() =>
                new CocoarAuthClaimsTransformation(http, distribution, cache,
                    Options.Create(new CocoarAuthOptions { AppSlug = "x", IdpBaseUrl = "x", ResourceServerId = "" }),
                    NullLogger<CocoarAuthClaimsTransformation>.Instance));

            Assert.Throws<InvalidOperationException>(() =>
                new CocoarAuthClaimsTransformation(http, distribution, cache,
                    Options.Create(new CocoarAuthOptions { AppSlug = "x", IdpBaseUrl = "x", ResourceServerId = "x", ResourceServerSecret = "" }),
                    NullLogger<CocoarAuthClaimsTransformation>.Instance));
        }
    }

    public class Caching
    {
        [Fact]
        public async Task Same_token_in_two_requests_hits_cache_only_once()
        {
            var response = new MePermissionsResponse(
                "alice", "cocoar-policy", ["policy:read"], [], []);
            var (subject, client) = NewSubject(response);

            // Two separate principal instances with the SAME sub+jti — the
            // second one would be a fresh request hitting the same cache key.
            await subject.TransformAsync(NewAuthenticatedPrincipal(sub: "alice", jti: "tok-1"));
            await subject.TransformAsync(NewAuthenticatedPrincipal(sub: "alice", jti: "tok-1"));

            Assert.Equal(1, client.CallCount);
        }

        [Fact]
        public async Task Different_jti_bypasses_cache()
        {
            // Token rotation invalidates the cache automatically via the
            // jti component of the key. New token → fresh distribution call.
            var response = new MePermissionsResponse(
                "alice", "cocoar-policy", ["policy:read"], [], []);
            var (subject, client) = NewSubject(response);

            await subject.TransformAsync(NewAuthenticatedPrincipal(sub: "alice", jti: "tok-1"));
            await subject.TransformAsync(NewAuthenticatedPrincipal(sub: "alice", jti: "tok-2"));

            Assert.Equal(2, client.CallCount);
        }
    }
}
