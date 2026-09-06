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

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed record ClientCredentials(string ClientId, string Secret, string? RedirectUri);

    private async Task<IHost> BuildResourceServerAsync(string audience, Guid appId, ClientCredentials management)
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
                    services.AddHttpClient("Modgud.ResourceServer.SessionFeed")
                        .ConfigurePrimaryHttpMessageHandler(() => Factory.Server.CreateHandler());
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
        Assert.True(response.IsSuccessStatusCode, $"service-account arrange failed ({(int)response.StatusCode}): {body}");
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
        Assert.True((int)authorize.StatusCode is >= 300 and < 400, $"authorize: {(int)authorize.StatusCode} {await authorize.Content.ReadAsStringAsync(ct)}");
        var code = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]
                   ?? throw new Xunit.Sdk.XunitException($"no code in {authorize.Headers.Location}");

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
        Assert.True(token.IsSuccessStatusCode, $"token: {(int)token.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }
}
