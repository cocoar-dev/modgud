using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Management;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Management;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// DPOP-B8-01 fix regression. Uses real issued bound
/// tokens through the Management HTTP pipeline. No tokens, proofs,
/// client secrets or response bodies are included in assertion diagnostics.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ManagementDpopBoundaryTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string WritePath = "/api/admin/oauth/clients";
    private const string TokenUri = "http://localhost/connect/token";

    [Theory]
    [InlineData(AccessTokenType.Jwt)]
    [InlineData(AccessTokenType.Reference)]
    public async Task Bound_management_token_requires_dpop_scheme_valid_binding_and_fresh_proof(AccessTokenType tokenType)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ArrangeClientAsync(tokenType);
        using var owner = new ProofKey();
        using var other = new ProofKey();
        var token = await IssueBoundAsync(client, owner);

        foreach (var variant in new[] { "correct", "missing", "wrong-key", "malformed", "wrong-ath", "wrong-method", "wrong-uri", "expired", "future", "overflow", "signature" })
        {
            var proof = variant switch
            {
                "correct" => owner.Create("POST", "http://localhost" + WritePath, token),
                "wrong-key" => other.Create("POST", "http://localhost" + WritePath, token),
                "wrong-ath" => owner.Create("POST", "http://localhost" + WritePath, "not-the-access-token"),
                "malformed" => "not-a-proof",
                "wrong-method" => owner.Create("GET", "http://localhost" + WritePath, token),
                "wrong-uri" => owner.Create("POST", "http://localhost/not-management", token),
                "expired" => owner.Create("POST", "http://localhost" + WritePath, token, iat: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()),
                "future" => owner.Create("POST", "http://localhost" + WritePath, token, iat: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()),
                "overflow" => owner.Create("POST", "http://localhost" + WritePath, token, iat: long.MaxValue),
                "signature" => TamperSignature(owner.Create("POST", "http://localhost" + WritePath, token)),
                _ => null,
            };
            // Independently prove that the positive proof is valid and the wrong
            // key is otherwise a valid proof (including ath), not malformed input.
            var validation = variant == "overflow" ? DpopValidationResult.Fail(DpopError.Malformed)
                : DpopProofValidator.Validate(proof, "POST", "http://localhost" + WritePath,
                    DateTimeOffset.UtcNow, accessToken: token);
            if (variant is "correct" or "wrong-key")
            {
                Assert.True(validation.IsValid, "Generated resource proof must validate independently.");
                Assert.True((validation.Jkt == owner.Jkt) == (variant == "correct"), "Proof key identity mismatch.");
            }
            else Assert.False(validation.IsValid);

            var childId = $"dpop-observed-{Guid.NewGuid():N}";
            using var response = await CreateViaManagementAsync(childId, token, proof, scheme: "DPoP");
            Assert.Equal(variant == "correct" ? HttpStatusCode.Created : HttpStatusCode.Unauthorized, response.StatusCode);
            if (variant == "correct")
            {
                await using var read = GetTenantedSession();
                Assert.True(await read.Query<OAuthApplicationState>()
                    .AnyAsync(c => c.ClientId == childId && !c.IsDeleted, ct), "Management write must actually persist.");
                var replayChild = $"dpop-replay-{Guid.NewGuid():N}";
                using var replay = await CreateViaManagementAsync(replayChild, token, proof, scheme: "DPoP");
                Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
                await AssertNotCreatedAsync(replayChild);
            }
            else await AssertNotCreatedAsync(childId);
        }

        // No downgrade to Bearer, even with the right proof or an admin cookie.
        foreach (var withProof in new[] { false, true })
        {
            var schemeChild = $"dpop-scheme-{Guid.NewGuid():N}";
            using var rejected = await CreateViaManagementAsync(schemeChild, token,
                withProof ? owner.Create("POST", "http://localhost" + WritePath, token) : null);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            await AssertNotCreatedAsync(schemeChild);
        }
        var cookieChild = $"dpop-no-cookie-rescue-{Guid.NewGuid():N}";
        using (var rejected = await CreateViaManagementAsync(cookieChild, token, proof: null, cookieClient: Client))
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        await AssertNotCreatedAsync(cookieChild);

        // Same bound token cannot leave its client's target App assignment.
        using (var allowed = await GetScopeAsync(client.AppId, token, owner))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var otherApp = await CreateAppAsync();
        using (var denied = await GetScopeAsync(otherApp, token, owner))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await AssertProblemCodeAsync(denied, "Management.ClientAppMismatch");
        }

        // An invalid access token remains invalid, regardless of a valid proof.
        var invalidChild = $"dpop-invalid-token-{Guid.NewGuid():N}";
        var invalidToken = tokenType == AccessTokenType.Jwt ? TamperSignature(token) : "invalid-reference-token";
        using (var denied = await CreateViaManagementAsync(invalidChild, invalidToken,
                   owner.Create("POST", "http://localhost" + WritePath, invalidToken), scheme: "DPoP"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await AssertNotCreatedAsync(invalidChild);
    }

    [Theory]
    [InlineData(AccessTokenType.Jwt)]
    [InlineData(AccessTokenType.Reference)]
    public async Task Bound_token_still_requires_live_permission_and_audience(AccessTokenType tokenType)
    {
        using var owner = new ProofKey();
        var deniedClient = await ArrangeClientAsync(tokenType, allowWrite: false);
        var deniedToken = await IssueBoundAsync(deniedClient, owner);
        var childId = $"dpop-no-permission-{Guid.NewGuid():N}";
        using (var denied = await CreateViaManagementAsync(childId, deniedToken,
                   owner.Create("POST", "http://localhost" + WritePath, deniedToken), scheme: "DPoP"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await AssertProblemCodeAsync(denied, "Management.PermissionDenied");
        }
        await AssertNotCreatedAsync(childId);

        var wrongAudience = await ArrangeClientAsync(tokenType, otherAudience: true);
        var otherToken = await IssueBoundAsync(wrongAudience, owner);
        var otherChild = $"dpop-wrong-audience-{Guid.NewGuid():N}";
        using (var denied = await CreateViaManagementAsync(otherChild, otherToken,
                   owner.Create("POST", "http://localhost" + WritePath, otherToken), scheme: "DPoP"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await AssertProblemCodeAsync(denied, "Management.InvalidAudience");
        }
        await AssertNotCreatedAsync(otherChild);
    }

    [Theory]
    [InlineData(AccessTokenType.Jwt)]
    [InlineData(AccessTokenType.Reference)]
    public async Task Issuance_enforces_dpop_requirement_while_unbound_bearer_clients_still_work(AccessTokenType tokenType)
    {
        var required = await ArrangeClientAsync(tokenType);
        using (var missing = await TokenRequestAsync(required, proof: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var body = await ParseAsync(missing);
            Assert.Equal("invalid_dpop_proof", body.RootElement.GetProperty("error").GetString());
        }
        using var owner = new ProofKey();
        using (var invalid = await TokenRequestAsync(required, owner.Create("POST", "https://wrong.example/token")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var body = await ParseAsync(invalid);
            Assert.Equal("invalid_dpop_proof", body.RootElement.GetProperty("error").GetString());
        }

        var ordinary = await ArrangeClientAsync(tokenType, requireDpop: false);
        using var response = await TokenRequestAsync(ordinary, proof: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var issued = await ParseAsync(response);
        Assert.Equal("Bearer", issued.RootElement.GetProperty("token_type").GetString());
        var token = issued.RootElement.GetProperty("access_token").GetString()!;
        using var introspection = await IntrospectAsync(ordinary, token);
        Assert.True(introspection.RootElement.GetProperty("active").GetBoolean());
        Assert.False(introspection.RootElement.TryGetProperty("cnf", out _));
        var childId = $"dpop-unbound-{Guid.NewGuid():N}";
        using var created = await CreateViaManagementAsync(childId, token, proof: null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var unboundDpopChild = $"dpop-unbound-scheme-{Guid.NewGuid():N}";
        using (var denied = await CreateViaManagementAsync(unboundDpopChild, token,
                   owner.Create("POST", "http://localhost" + WritePath, token), scheme: "DPoP"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await AssertNotCreatedAsync(unboundDpopChild);
        using var cookieWrite = await Client.PostAsJsonAsync(WritePath, new CreateOAuthClientDto
        {
            ClientId = $"dpop-cookie-{Guid.NewGuid():N}", ClientType = OAuthClientTypes.Public,
            RequireClientSecret = false, AllowedGrantTypes = ["authorization_code"],
            Scopes = ["openid"], RedirectUris = ["https://dpop-test.example/callback"],
        }, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, cookieWrite.StatusCode);
    }

    [Theory]
    [InlineData(AccessTokenType.Jwt)]
    [InlineData(AccessTokenType.Reference)]
    public async Task Parallel_replay_executes_at_most_once_and_failed_consumers_do_not_release_proofs(AccessTokenType tokenType)
    {
        var client = await ArrangeClientAsync(tokenType);
        using var owner = new ProofKey();
        var token = await IssueBoundAsync(client, owner);
        var proof = owner.Create("POST", "http://localhost" + WritePath, token);
        var firstId = $"dpop-parallel-a-{Guid.NewGuid():N}";
        var secondId = $"dpop-parallel-b-{Guid.NewGuid():N}";
        var responses = await Task.WhenAll(
            CreateViaManagementAsync(firstId, token, proof, scheme: "DPoP"),
            CreateViaManagementAsync(secondId, token, proof, scheme: "DPoP"));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var read = GetTenantedSession();
        var persisted = await read.Query<OAuthApplicationState>()
            .Where(c => c.ClientId == firstId || c.ClientId == secondId).ToListAsync(TestContext.Current.CancellationToken);
        var existing = Assert.Single(persisted).ClientId;
        var failedProof = owner.Create("POST", "http://localhost" + WritePath, token);
        using (var conflict = await CreateViaManagementAsync(existing, token, failedProof, scheme: "DPoP"))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var retryId = $"dpop-after-failure-{Guid.NewGuid():N}";
        using (var reused = await CreateViaManagementAsync(retryId, token, failedProof, scheme: "DPoP"))
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        await AssertNotCreatedAsync(retryId);
        using var fresh = await CreateViaManagementAsync(retryId, token,
            owner.Create("POST", "http://localhost" + WritePath, token), scheme: "DPoP");
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
    }

    [Fact]
    public async Task Replay_is_durable_in_a_fresh_scope_without_committing_pending_request_writes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = new ProofKey();
        const string token = "test-only-direct-validator-token";
        var proof = key.Create("POST", "http://localhost" + WritePath, token);
        var validated = DpopProofValidator.Validate(proof, "POST", "http://localhost" + WritePath,
            DateTimeOffset.UtcNow, accessToken: token);
        Assert.True(validated.IsValid);
        var sentinel = "pending-only-" + Guid.NewGuid().ToString("N");
        await using (var requestScope = Factory.Services.CreateAsyncScope())
        {
            var session = requestScope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Insert(new DpopReplayEntry { Id = sentinel, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
            var http = ProofContext(requestScope.ServiceProvider, proof, ct);
            Assert.True(await requestScope.ServiceProvider.GetRequiredService<ManagementDpopProofValidator>()
                .ValidateAsync(http, ProofCaller(key), new("DPoP", token)));
            await using var read = GetTenantedSession();
            Assert.NotNull(await read.LoadAsync<DpopReplayEntry>(validated.Jti!, ct));
            Assert.Null(await read.LoadAsync<DpopReplayEntry>(sentinel, ct));
            // Deliberately discard the request session without SaveChanges.
        }
        await using var retryScope = Factory.Services.CreateAsyncScope();
        Assert.False(await retryScope.ServiceProvider.GetRequiredService<ManagementDpopProofValidator>()
            .ValidateAsync(ProofContext(retryScope.ServiceProvider, proof, ct), ProofCaller(key), new("DPoP", token)));
        await using var finalRead = GetTenantedSession();
        Assert.NotNull(await finalRead.LoadAsync<DpopReplayEntry>(validated.Jti!, ct));
        Assert.Null(await finalRead.LoadAsync<DpopReplayEntry>(sentinel, ct));
    }

    [Fact]
    public async Task Ambient_apply_cannot_turn_a_replay_commit_into_an_uncommitted_flush()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = new ProofKey();
        const string token = "test-only-direct-validator-token";
        var proof = key.Create("POST", "http://localhost" + WritePath, token);
        await using var requestScope = Factory.Services.CreateAsyncScope();
        var scopes = new CountingScopes(Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var validator = new ManagementDpopProofValidator(scopes, TimeProvider.System);
        var http = ProofContext(requestScope.ServiceProvider, proof, ct);
        await using (var apply = await TenantApplyTransaction.BeginAsync(
                         Factory.Services.GetRequiredService<IDocumentStore>(), "system", ct))
        {
            using (apply.Activate())
            {
                Assert.False(await validator.ValidateAsync(http, ProofCaller(key), new("DPoP", token)));
                Assert.Equal(0, scopes.Created);
            }
        }
        Assert.Null(TenantApplyTransaction.Current);
        Assert.True(await validator.ValidateAsync(http, ProofCaller(key), new("DPoP", token)));
        Assert.Equal(1, scopes.Created);
    }

    [Fact]
    public async Task Invalid_or_multiple_authorization_headers_cannot_be_rescued_by_an_admin_cookie()
    {
        var appId = await CreateAppAsync();
        var path = $"/api/app/{ShortGuid.Encode(appId)}/scope";
        using (var allowed = await Client.GetAsync(path, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        foreach (var headers in new[] { new[] { "Basic invalid" }, new[] { "not-an-authorization" },
                     new[] { "Bearer invalid", "DPoP invalid" } })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            Assert.True(request.Headers.TryAddWithoutValidation("Authorization", headers));
            using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private sealed class CountingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created { get; private set; }
        public IServiceScope CreateScope() { Created++; return inner.CreateScope(); }
    }

    private static ClaimsPrincipal ProofCaller(ProofKey key) => new(new ClaimsIdentity(
        [new Claim("cnf", JsonSerializer.Serialize(new { jkt = key.Jkt }))], "direct-validator-test"));

    private static DefaultHttpContext ProofContext(IServiceProvider services, string proof, CancellationToken ct)
    {
        var http = new DefaultHttpContext { RequestServices = services, RequestAborted = ct };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.Request.Method = "POST";
        http.Request.Path = WritePath;
        http.Request.Headers["DPoP"] = proof;
        return http;
    }

    private sealed record Credentials(string ClientId, string Secret, Guid AppId, string Scope, string Audience, AccessTokenType TokenType);

    private async Task<Credentials> ArrangeClientAsync(AccessTokenType tokenType, bool allowWrite = true,
        bool requireDpop = true, bool otherAudience = false)
    {
        var ct = TestContext.Current.CancellationToken;
        using var accountResponse = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = $"dpop-management-{Guid.NewGuid():N}" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, accountResponse.StatusCode);
        var account = await accountResponse.Content.ReadFromJsonAsync<ServiceAccountDto>(JsonOptions, ct);
        Assert.NotNull(account);
        var principalId = new ShortGuid(account.Id).Guid;
        var permissions = new List<(string, string)> { ("app-scope", "read") };
        if (allowWrite) permissions.Add(("oauth-client", "write"));
        var role = await Factory.CreateTestRoleAsync($"DpopManagement_{Guid.NewGuid():N}", permissions);
        await Factory.CreateTestGroupAsync($"DpopManagement_{Guid.NewGuid():N}", [principalId], [role.Id]);
        var appId = await CreateAppAsync();
        var scopeName = ModgudManagementApi.Scope;
        var audience = ModgudManagementApi.Audience;
        using var scope = Factory.Services.CreateScope();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        if (otherAudience)
        {
            scopeName = $"dpop-other-{Guid.NewGuid():N}";
            audience = "urn:modgud:test:dpop-other";
            var created = await oauth.CreateScopeAsync(new CreateOAuthScopeDto
            {
                Name = scopeName, DisplayName = scopeName, Resources = [audience],
            }, ct);
            Assert.False(created.IsError, "Other-audience scope setup failed.");
        }
        var clientId = $"dpop-management-{Guid.NewGuid():N}";
        var secret = $"test-only-{Guid.NewGuid():N}";
        var result = await oauth.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId, ClientSecret = secret, ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit, DisplayName = clientId,
            Scopes = [scopeName], AllowedGrantTypes = ["client_credentials"],
            AccessTokenType = tokenType, RequireDpop = requireDpop, RequireConsent = false,
            AppIds = [ShortGuid.Encode(appId)], LinkedServiceAccountId = account.Id,
        }, ct);
        Assert.False(result.IsError, "Management client setup failed.");
        return new(clientId, secret, appId, scopeName, audience, tokenType);
    }

    private async Task<Guid> CreateAppAsync()
    {
        var appId = Guid.NewGuid();
        await using var session = GetTenantedDocumentSession();
        session.Events.StartStream<App>(appId, new AppCreatedEvent(appId,
            $"dpop-app-{Guid.NewGuid():N}", "DPoP test App", null, [], false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return appId;
    }

    private async Task<string> IssueBoundAsync(Credentials client, ProofKey owner)
    {
        using var response = await TokenRequestAsync(client, owner.Create("POST", TokenUri));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ParseAsync(response);
        Assert.Equal("DPoP", body.RootElement.GetProperty("token_type").GetString());
        var token = body.RootElement.GetProperty("access_token").GetString()!;
        Assert.False(string.IsNullOrEmpty(token));
        if (client.TokenType == AccessTokenType.Jwt)
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[1]));
            Assert.True(payload.RootElement.GetProperty("cnf").GetProperty("jkt").GetString() == owner.Jkt,
                "Issued JWT must be bound to the original proof key.");
        }
        else Assert.False(token.Contains('.'), "Reference-token case must not silently become JWT.");
        using var intro = await IntrospectAsync(client, token);
        Assert.True(intro.RootElement.GetProperty("active").GetBoolean());
        Assert.True(intro.RootElement.GetProperty("cnf").GetProperty("jkt").GetString() == owner.Jkt,
            "Introspection must independently expose the issued token binding.");
        return token;
    }

    private async Task<HttpResponseMessage> TokenRequestAsync(Credentials client, string? proof)
    {
        using var http = AnonymousHttp();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = client.ClientId,
                ["client_secret"] = client.Secret, ["scope"] = client.Scope, ["resource"] = client.Audience,
            }),
        };
        if (proof is not null) request.Headers.TryAddWithoutValidation("DPoP", proof);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<JsonDocument> IntrospectAsync(Credentials client, string token)
    {
        using var http = AnonymousHttp();
        using var response = await http.PostAsync("/connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["client_secret"] = client.Secret,
            ["token"] = token, ["token_type_hint"] = "access_token",
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ParseAsync(response);
    }

    private async Task<HttpResponseMessage> CreateViaManagementAsync(string childId, string token, string? proof,
        string scheme = "Bearer", HttpClient? cookieClient = null)
    {
        using var anonymous = AnonymousHttp();
        var http = cookieClient ?? anonymous;
        using var request = new HttpRequestMessage(HttpMethod.Post, WritePath)
        {
            Content = JsonContent.Create(new CreateOAuthClientDto
            {
                ClientId = childId, ClientType = OAuthClientTypes.Public, RequireClientSecret = false,
                AllowedGrantTypes = ["authorization_code"], Scopes = ["openid"],
                RedirectUris = ["https://dpop-test.example/callback"], RequireConsent = false,
            }, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        if (proof is not null) request.Headers.TryAddWithoutValidation("DPoP", proof);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> GetScopeAsync(Guid appId, string token, ProofKey? key = null)
    {
        using var http = AnonymousHttp();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/app/{ShortGuid.Encode(appId)}/scope");
        request.Headers.Authorization = new AuthenticationHeaderValue(key is null ? "Bearer" : "DPoP", token);
        if (key is not null) request.Headers.Add("DPoP", key.Create("GET", "http://localhost" + request.RequestUri, token));
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task AssertNotCreatedAsync(string childId)
    {
        await using var read = GetTenantedSession();
        Assert.False(await read.Query<OAuthApplicationState>().AnyAsync(c => c.ClientId == childId,
            TestContext.Current.CancellationToken), "Rejected request must not create a client.");
    }

    private HttpClient AnonymousHttp() => Factory.CreateClient(new()
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = await ParseAsync(response);
        Assert.Equal(expected, json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static string TamperSignature(string token)
    {
        var start = token.LastIndexOf('.') + 1;
        return token[..start] + (token[start] == 'A' ? "B" : "A") + token[(start + 1)..];
    }

    private sealed class ProofKey : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Jkt { get; }
        public ProofKey()
        {
            var p = _key.ExportParameters(false);
            Jkt = JwkThumbprint.ForEc("P-256", p.Q.X!, p.Q.Y!);
        }
        public string Create(string method, string uri, string? accessToken = null, long? iat = null)
        {
            var p = _key.ExportParameters(false);
            var header = new { typ = "dpop+jwt", alg = "ES256", jwk = new
            {
                kty = "EC", crv = "P-256", x = B64(p.Q.X!), y = B64(p.Q.Y!),
            } };
            var payload = new Dictionary<string, object>
            {
                ["jti"] = Guid.NewGuid().ToString("N"), ["htm"] = method, ["htu"] = uri,
                ["iat"] = iat ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (accessToken is not null) payload["ath"] = B64(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
            var signing = B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(payload));
            return signing + "." + B64(_key.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);
        public void Dispose() => _key.Dispose();
    }
}
