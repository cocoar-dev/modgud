using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.AspNetCore.ResourceServer;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.ChangeFeed;
using Modgud.Api.Features.ChangeFeed;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR 0021 increment 2 — a resource server on <c>Modgud.AspNetCore.ResourceServer</c>
/// that validates JWTs locally learns about ended sessions from the Application change
/// feed and rejects their tokens before expiry.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SessionRevocationResourceServerTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Password = "TestPass1234";

    [Fact]
    public async Task A_jwt_is_rejected_once_the_feed_reports_the_session_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var audience = $"https://rs-{suffix}.example";

        // The App, its API/scope, the user in scope and the relying-party client.
        var acronym = $"sr{suffix}"[..10];
        var user = await Factory.CreateTestUserWithIdentityAsync("Session", "Revocation", acronym, $"{acronym}@sr.example", Password);
        var app = await CreateAppAsync($"sessrev-{suffix}", "Session revocation");
        await CreateOAuthApiAsync(audience, app.Id);
        var scopeName = $"sessrev-{suffix}.read";
        await CreateScopeAsync(scopeName, [audience], app.Id);
        await Factory.CreateTestGroupAsync($"sessrev-scope-{suffix}", [user.Id], [], boundTo: [app.Slug]);
        var rp = await CreateClientAsync($"sessrev-rp-{suffix}", ["openid", scopeName], ["authorization_code", "refresh_token"], app.Id, redirect: $"https://rp-{suffix}.example/cb");

        // The resource server's own management client (client_credentials, app-scope:read on the App).
        var serviceAccount = await CreateServiceAccountAsync($"sessrev-reader-{suffix}");
        var readerRole = await Factory.CreateTestRoleAsync($"SessRevReader_{suffix}", [("app-scope", "read")]);
        await Factory.CreateTestGroupAsync($"SessRevReaders_{suffix}", [new ShortGuid(serviceAccount.Id).Guid], [readerRole.Id]);
        var management = await CreateClientAsync($"sessrev-mgmt-{suffix}", ["modgud.management"], ["client_credentials"], app.Id, linkedServiceAccountId: serviceAccount.Id);

        await EnableFeedAsync(app.Id);
        await Factory.WaitForProjectionsAsync();

        using var resourceServer = await BuildResourceServerAsync(audience, app.Id, management);
        var denylist = resourceServer.Services.GetRequiredService<IModgudSessionDenylist>();

        // A live session: the token is accepted.
        var cookieClient = await CreateAuthenticatedClientAsync(acronym, Password);
        var accessToken = await DriveAuthCodeFlowAsync(cookieClient, rp, $"openid {scopeName}", audience);
        var sid = new JsonWebToken(accessToken).GetClaim("sid").Value;
        await Factory.WaitForProjectionsAsync();

        var api = resourceServer.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await CallMeAsync(api, accessToken)).StatusCode);

        // The worker synced at least once (it anchors at a fresh snapshot cursor).
        await WaitUntilAsync(() => denylist.LastSyncedAt is not null, "first feed sync");
        Assert.False(denylist.IsRevoked(sid));

        // The user signs out: the feed emits the session end, the denylist picks it up.
        (await cookieClient.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();
        await WaitUntilAsync(() => denylist.IsRevoked(sid), "session end on the denylist");

        var refused = await CallMeAsync(api, accessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public void Enabling_revocation_without_the_feed_credentials_fails_at_startup()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            services.AddModgudResourceServer(options =>
            {
                options.Authority = "https://id.example";
                options.Audience = "https://api.example";
                options.SessionRevocation = new ModgudSessionRevocationOptions { Enabled = true };
            }));
        Assert.Contains(ex.Failures, f => f.Contains("AppId", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.Contains("ClientId", StringComparison.Ordinal));

        var referenceOnly = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            new ServiceCollection().AddModgudResourceServer(options =>
            {
                options.Authority = "https://id.example";
                options.Audience = "https://api.example";
                options.TokenMode = ModgudTokenMode.OnlyReferenceToken;
                options.IntrospectionClientSecret = "s";
                options.SessionRevocation = new ModgudSessionRevocationOptions { Enabled = true, AppId = "x" };
            }));
        Assert.Contains(referenceOnly.Failures, f => f.Contains("JWT access tokens only", StringComparison.Ordinal));
    }

    // SDK-B5-01 regression: recover actual public-sid ends, never infer an
    // end merely from absence in the current entity snapshot.
    [Theory]
    [InlineData("restart")]
    [InlineData("cursor-reset")]
    [InlineData("transport-retry")]
    public async Task Historical_ends_survive_restart_reset_and_transport_retry(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var audience = $"https://rs-{suffix}.example";
        var userA = await Factory.CreateTestUserWithIdentityAsync("Known", "End", $"a{suffix}", $"a{suffix}@sr.example", Password);
        var userB = await Factory.CreateTestUserWithIdentityAsync("Unknown", "End", $"b{suffix}", $"b{suffix}@sr.example", Password);
        var userC = await Factory.CreateTestUserWithIdentityAsync("Still", "Active", $"c{suffix}", $"c{suffix}@sr.example", Password);
        var app = await CreateAppAsync($"history-{suffix}", "Session history");
        await CreateOAuthApiAsync(audience, app.Id);
        var scopeName = $"history-{suffix}.read";
        await CreateScopeAsync(scopeName, [audience], app.Id);
        await Factory.CreateTestGroupAsync($"history-users-{suffix}", [userA.Id, userB.Id, userC.Id], [], boundTo: [app.Slug]);
        var rp = await CreateClientAsync($"history-rp-{suffix}", ["openid", scopeName],
            ["authorization_code", "refresh_token"], app.Id, redirect: $"https://rp-{suffix}.example/cb");
        var serviceAccount = await CreateServiceAccountAsync($"history-reader-{suffix}");
        var role = await Factory.CreateTestRoleAsync($"HistoryReader_{suffix}", [("app-scope", "read")]);
        await Factory.CreateTestGroupAsync($"HistoryReaders_{suffix}", [new ShortGuid(serviceAccount.Id).Guid], [role.Id]);
        var management = await CreateClientAsync($"history-mgmt-{suffix}", ["modgud.management"],
            ["client_credentials"], app.Id, linkedServiceAccountId: serviceAccount.Id);
        await EnableFeedAsync(app.Id);
        await Factory.WaitForProjectionsAsync();

        using var cookieA = await CreateAuthenticatedClientAsync($"a{suffix}", Password);
        using var cookieB = await CreateAuthenticatedClientAsync($"b{suffix}", Password);
        using var cookieC = await CreateAuthenticatedClientAsync($"c{suffix}", Password);
        var tokenA = await DriveAuthCodeFlowAsync(cookieA, rp, $"openid {scopeName}", audience);
        var tokenB = await DriveAuthCodeFlowAsync(cookieB, rp, $"openid {scopeName}", audience);
        var tokenC = await DriveAuthCodeFlowAsync(cookieC, rp, $"openid {scopeName}", audience);
        var sidA = new JsonWebToken(tokenA).GetClaim("sid").Value;
        var sidB = new JsonWebToken(tokenB).GetClaim("sid").Value;
        Assert.NotEqual(sidA, sidB);
        await Factory.WaitForProjectionsAsync();

        string liveCursor;
        using (var scope = Factory.Services.CreateScope())
        {
            var query = new AppChangeFeedQueryService(scope.ServiceProvider.GetRequiredService<IDocumentSession>());
            var live = await query.SnapshotAsync(app.Id, ct);
            Assert.True(live.IsSuccess);
            Assert.Contains(live.Value!.Entities, e => e.EntityKind == "session" && e.Payload.GetProperty("SessionId").GetString() == sidA);
            Assert.Contains(live.Value.Entities, e => e.EntityKind == "session" && e.Payload.GetProperty("SessionId").GetString() == sidB);
            liveCursor = live.Value.Cursor;
        }

        var transport = new FeedTransportControl();
        using var original = await BuildResourceServerAsync(audience, app.Id, management, manualFeed: true, transport);
        var worker = original.Services.GetRequiredService<ModgudSessionRevocationWorker>();
        var denylist = original.Services.GetRequiredService<IModgudSessionDenylist>();
        var api = original.GetTestClient();
        await worker.StepAsync(ct);
        Assert.NotNull(denylist.LastSyncedAt);
        Assert.Equal(HttpStatusCode.OK, (await CallMeAsync(api, tokenA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallMeAsync(api, tokenB)).StatusCode);

        // A is known to the running process; B ends while this consumer is not
        // polling. Both ends are generated by real browser logout, not feed JSON.
        (await cookieA.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();
        await worker.StepAsync(ct);
        Assert.True(denylist.IsRevoked(sidA));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(api, tokenA)).StatusCode);
        (await cookieB.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();
        Assert.False(denylist.IsRevoked(sidB));

        // The actual snapshot/checkpoint now lies after both ends and contains
        // neither session. A resumed read cannot reconstruct these older ends.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var query = new AppChangeFeedQueryService(session);
            var retained = await query.ReadAsync(app.Id, liveCursor, 500, ct);
            Assert.True(retained.IsSuccess);
            Assert.Contains(retained.Value!.Messages, m => m.EntityKind == "session" && m.ChangeKind == "Deleted"
                && m.Payload!.Value.GetProperty("SessionId").GetString() == sidA);
            Assert.Contains(retained.Value.Messages, m => m.EntityKind == "session" && m.ChangeKind == "Deleted"
                && m.Payload!.Value.GetProperty("SessionId").GetString() == sidB);
            var snapshot = await query.SnapshotAsync(app.Id, ct);
            Assert.True(snapshot.IsSuccess);
            Assert.DoesNotContain(snapshot.Value!.Entities, e => e.EntityKind == "session"
                && (e.Payload.GetProperty("SessionId").GetString() == sidA || e.Payload.GetProperty("SessionId").GetString() == sidB));
            var after = await query.ReadAsync(app.Id, snapshot.Value.Cursor, 500, ct);
            Assert.True(after.IsSuccess);
            Assert.DoesNotContain(after.Value!.Messages, m => m.EntityKind == "session" && m.ChangeKind == "Deleted");
        }

        if (mode == "restart")
        {
            // A second independent service provider models fresh process memory;
            // it is not a literal OS process kill or a deployment topology test.
            using var restarted = await BuildResourceServerAsync(audience, app.Id, management, manualFeed: true);
            var fresh = restarted.Services.GetRequiredService<IModgudSessionDenylist>();
            await restarted.Services.GetRequiredService<ModgudSessionRevocationWorker>().StepAsync(ct);
            Assert.NotNull(fresh.LastSyncedAt);
            Assert.True(fresh.IsRevoked(sidA));
            Assert.True(fresh.IsRevoked(sidB));
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(restarted.GetTestClient(), tokenA)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(restarted.GetTestClient(), tokenB)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await CallMeAsync(restarted.GetTestClient(), tokenC)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(api, tokenA)).StatusCode);
        }
        else if (mode == "cursor-reset")
        {
            // Move only this fresh test DB's retained resume floor past the
            // consumer cursor. This exercises a real server CursorTooOld/409,
            // without waiting for the scheduled retention pruner.
            using (var scope = Factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var state = (await session.LoadAsync<AppChangeFeedState>(app.Id, ct))!;
                state.RetentionFloorSequence = state.LastProcessedSequence;
                state.RetentionFloorOrdinal = 0;
                session.Store(state);
                await session.SaveChangesAsync(ct);
            }
            Assert.Equal(TimeSpan.Zero, await worker.StepAsync(ct));
            Assert.Equal(1, transport.Conflicts);
            Assert.Equal("CursorTooOld", transport.LastConflictCode);
            // PollInterval, not Zero: the second step really reanchored and
            // completed a nonterminal/caught-up read, not another 409/reset.
            Assert.Equal(TimeSpan.FromMilliseconds(200), await worker.StepAsync(ct));
            Assert.Equal(1, transport.Conflicts);
            Assert.NotNull(denylist.LastSyncedAt);
            Assert.True(denylist.IsRevoked(sidA));
            Assert.True(denylist.IsRevoked(sidB));
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(api, tokenA)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(api, tokenB)).StatusCode);
        }
        else
        {
            transport.FailNextRead = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => worker.StepAsync(ct));
            Assert.False(transport.FailNextRead);
            Assert.True(denylist.IsRevoked(sidA));
            Assert.False(denylist.IsRevoked(sidB));
            await worker.StepAsync(ct); // unchanged cursor resumes, B is learned
            Assert.True(denylist.IsRevoked(sidB));
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(api, tokenA)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(api, tokenB)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await CallMeAsync(api, tokenC)).StatusCode);

        // No universal JWT bypass: actual audience and signature gates still
        // reject this otherwise accepted token even with a fresh empty denylist.
        using var otherAudience = await BuildResourceServerAsync(audience + "/other", app.Id, management, manualFeed: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(otherAudience.GetTestClient(), tokenB)).StatusCode);
        var signatureStart = tokenB.LastIndexOf('.') + 1;
        var tampered = tokenB[..signatureStart] + (tokenB[signatureStart] == 'A' ? "B" : "A") + tokenB[(signatureStart + 1)..];
        using var signatureHost = await BuildResourceServerAsync(audience, app.Id, management, manualFeed: true);
        Assert.Equal(HttpStatusCode.OK, (await CallMeAsync(signatureHost.GetTestClient(), tokenB)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallMeAsync(signatureHost.GetTestClient(), tampered)).StatusCode);
        Assert.True(new JsonWebToken(tokenB).ValidTo > DateTime.UtcNow);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed record ClientCredentials(string ClientId, string Secret, string? RedirectUri);

    private async Task<IHost> BuildResourceServerAsync(string audience, Guid appId, ClientCredentials management,
        bool manualFeed = false, FeedTransportControl? transport = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddModgudResourceServer(options =>
                    {
                        options.Authority = "http://localhost";
                        options.Audience = audience;
                        options.RequireHttpsMetadata = false;
                        options.ConfigureJwtBearer = jwt =>
                        {
                            jwt.MapInboundClaims = false;
                            jwt.BackchannelHttpHandler = Factory.Server.CreateHandler();
                        };
                        options.SessionRevocation = new ModgudSessionRevocationOptions
                        {
                            Enabled = true,
                            AppId = ShortGuid.Encode(appId),
                            ClientId = management.ClientId,
                            ClientSecret = management.Secret,
                            PollInterval = TimeSpan.FromMilliseconds(200),
                            RetryDelay = TimeSpan.FromMilliseconds(500),
                        };
                    });
                    // Keep the real registered singleton worker, but drive its
                    // individual steps deterministically in the history tests.
                    if (manualFeed) services.RemoveAll<IHostedService>();
                    services.AddHttpClient("Modgud.ResourceServer.SessionFeed")
                        .ConfigurePrimaryHttpMessageHandler(() => transport is null
                            ? Factory.Server.CreateHandler()
                            : new ControlledFeedHandler(transport) { InnerHandler = Factory.Server.CreateHandler() });
                    services.AddAuthorization();
                })
                .Configure(builder =>
                {
                    builder.UseRouting();
                    builder.UseAuthentication();
                    builder.UseAuthorization();
                    builder.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
                        {
                            sub = principal.FindFirst("sub")?.Value,
                            sid = principal.FindFirst(ModgudClaimTypes.SessionId)?.Value,
                        })).RequireAuthorization();
                    });
                }))
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class FeedTransportControl
    {
        public bool FailNextRead { get; set; }
        public int Conflicts { get; set; }
        public string? LastConflictCode { get; set; }
    }

    private sealed class ControlledFeedHandler(FeedTransportControl control) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var read = request.RequestUri!.AbsolutePath.EndsWith("/change-feed", StringComparison.Ordinal);
            if (read && control.FailNextRead)
            {
                control.FailNextRead = false;
                throw new HttpRequestException("Deliberate isolated feed transport failure.");
            }
            var response = await base.SendAsync(request, ct);
            if (read && response.StatusCode == HttpStatusCode.Conflict)
            {
                control.Conflicts++;
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                control.LastConflictCode = problem.RootElement.GetProperty("code").GetString();
            }
            return response;
        }
    }

    private static async Task<HttpResponseMessage> CallMeAsync(HttpClient api, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private async Task<App> CreateAppAsync(string slug, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            id, slug, displayName, null, [], false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task CreateOAuthApiAsync(string name, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var app = await session.LoadAsync<App>(appId, TestContext.Current.CancellationToken)
                  ?? throw new InvalidOperationException("app missing");
        var id = Guid.NewGuid();
        var (aggregate, created) = OAuthApiAggregate.Create(id, name, displayName: name, description: null, enabled: true, scopes: Array.Empty<string>());
        session.Events.StartStream<OAuthApiAggregate>(id, created);
        session.Events.Append(id, aggregate.SetAppId(appId));
        session.Events.Append(id, aggregate.SetPermissionIds(app.Permissions.Select(p => p.Id).ToList()));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task CreateScopeAsync(string name, List<string> resources, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = name, DisplayName = name, Resources = resources, AppId = ShortGuid.Encode(appId),
        }, TestContext.Current.CancellationToken);
        if (result.IsError) throw new InvalidOperationException(result.FirstError.Description);
    }

    private async Task<ClientCredentials> CreateClientAsync(
        string clientId, List<string> scopes, List<string> grants, Guid appId,
        string? redirect = null, string? linkedServiceAccountId = null)
    {
        var secret = $"{clientId}-secret";
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = redirect is null ? [] : [redirect],
            PostLogoutRedirectUris = [],
            Scopes = scopes,
            AllowedGrantTypes = grants,
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [ShortGuid.Encode(appId)],
            LinkedServiceAccountId = linkedServiceAccountId,
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
        return new ClientCredentials(clientId, secret, redirect);
    }

    private async Task<ServiceAccountDto> CreateServiceAccountAsync(string accountName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await Client.PostAsJsonAsync("/api/service-account", new { AccountName = accountName }, JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"service-account arrange failed ({(int)response.StatusCode}).");
        return JsonSerializer.Deserialize<ServiceAccountDto>(body, JsonOptions)!;
    }

    private async Task EnableFeedAsync(Guid appId)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Client.PutAsJsonAsync(
            $"/api/app/{ShortGuid.Encode(appId)}",
            new UpdateAppDto("Session revocation", null, [], new ApplicationSettingsDto
            {
                ChangeFeed = new ApplicationChangeFeedDto { Enabled = true, MinimumRetentionAgeDays = 7, MinimumEventCount = 1_000 },
            }),
            JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> DriveAuthCodeFlowAsync(HttpClient cookieClient, ClientCredentials rp, string scope, string resource)
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorizeUri = "/connect/authorize?" + string.Join("&",
        [
            "response_type=code",
            $"client_id={Uri.EscapeDataString(rp.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(rp.RedirectUri!)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={Guid.NewGuid():N}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(resource)}",
        ]);
        var authorize = await cookieClient.GetAsync(authorizeUri, ct);
        Assert.True((int)authorize.StatusCode is >= 300 and < 400, $"authorize: {(int)authorize.StatusCode}");
        var code = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]
                   ?? throw new Xunit.Sdk.XunitException("No authorization code in redirect.");

        var token = await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("client_id", rp.ClientId),
            new("client_secret", rp.Secret),
            new("redirect_uri", rp.RedirectUri!),
            new("code_verifier", verifier),
            new("resource", resource),
        ]), ct);
        var body = await token.Content.ReadAsStringAsync(ct);
        Assert.True(token.IsSuccessStatusCode, $"token: {(int)token.StatusCode}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }
}
