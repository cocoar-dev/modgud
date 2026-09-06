using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.BackChannelLogout;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.ChangeFeed;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR 0021 — <c>sid</c> in every token of a session, the OpenID Connect Back-Channel
/// Logout 1.0 transport (signed logout token by POST, delivery status, retries) and the
/// <c>session</c> entity kind of the Application change feed.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class BackChannelLogoutTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Password = "TestPass1234";

    private RecordingBackChannelLogoutSink Sink => Factory.Services.GetRequiredService<RecordingBackChannelLogoutSink>();

    // ── sid ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Browser_tokens_carry_the_browser_session_id_as_sid_and_keep_it_across_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, userName) = await CreateUserAsync("sid");
        var rp = await CreateRelyingPartyAsync("sid", AccessTokenType.Jwt, logoutUri: null);

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);

        var accessToken = new JsonWebToken(tokens.RootElement.GetProperty("access_token").GetString()!);
        var idToken = new JsonWebToken(tokens.RootElement.GetProperty("id_token").GetString()!);
        var sid = accessToken.GetClaim("sid").Value;
        Assert.Equal(sid, idToken.GetClaim("sid").Value);

        await using (var query = GetTenantedSession())
        {
            var sessions = await query.Query<UserSession>().Where(s => s.UserId == user).Select(s => s.Id).ToListAsync(ct);
            Assert.Contains(Guid.Parse(sid), sessions);

            var grant = await query.LoadAsync<SessionGrant>(SessionGrant.IdFor(Guid.Parse(sid), rp.ClientId), ct);
            Assert.NotNull(grant);
            Assert.Equal(user, grant!.UserId);
            Assert.Equal(accessToken.Issuer, grant.Issuer);
            Assert.Equal(Modgud.Authentication.Events.AccessSessionKind.Browser, grant.Kind);
        }

        var refreshed = await RefreshAsync(tokens.RootElement.GetProperty("refresh_token").GetString()!, rp);
        Assert.Equal(sid, new JsonWebToken(refreshed.RootElement.GetProperty("access_token").GetString()!).GetClaim("sid").Value);
    }

    [Fact]
    public async Task Introspection_returns_sid_for_reference_tokens()
    {
        var (_, userName) = await CreateUserAsync("intro");
        var rp = await CreateRelyingPartyAsync("intro", AccessTokenType.Reference, logoutUri: null);
        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        var sid = new JsonWebToken(tokens.RootElement.GetProperty("id_token").GetString()!).GetClaim("sid").Value;

        var introspection = await IntrospectAsync(rp, tokens.RootElement.GetProperty("access_token").GetString()!);
        Assert.True(introspection.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(sid, introspection.RootElement.GetProperty("sid").GetString());
    }

    // ── transport A ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_posts_a_signed_logout_token_to_every_relying_party_of_the_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, userName) = await CreateUserAsync("bcl");
        var alpha = await CreateRelyingPartyAsync("alpha", AccessTokenType.Jwt, logoutUri: SinkUri("alpha"));
        var beta = await CreateRelyingPartyAsync("beta", AccessTokenType.Jwt, logoutUri: SinkUri("beta"));

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var alphaTokens = await DriveAuthCodeFlowAsync(cookieClient, alpha);
        using var betaTokens = await DriveAuthCodeFlowAsync(cookieClient, beta);
        var alphaId = new JsonWebToken(alphaTokens.RootElement.GetProperty("id_token").GetString()!);
        var sid = alphaId.GetClaim("sid").Value;

        var logout = await cookieClient.PostAsJsonAsync("/api/account/logout", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        // The test host has no background daemon: run the fan-out subscription now.
        await Factory.WaitForProjectionsAsync();

        var toAlpha = await Sink.WaitForAsync(alpha.LogoutUri!, 1);
        var toBeta = await Sink.WaitForAsync(beta.LogoutUri!, 1);
        var alphaDelivery = Assert.Single(toAlpha);
        Assert.Single(toBeta);
        Assert.Contains("no-store", alphaDelivery.CacheControl ?? "");

        var logoutToken = await ValidateLogoutTokenAsync(alphaDelivery.LogoutToken, alphaId.Issuer, alpha.ClientId);
        Assert.Equal(user.ToString(), logoutToken.Subject);
        Assert.Equal(sid, logoutToken.GetClaim("sid").Value);
        Assert.Equal(LogoutTokenMinter.TokenType, logoutToken.Typ);
        Assert.True(logoutToken.TryGetClaim("jti", out _));
        Assert.False(logoutToken.TryGetClaim("nonce", out _));
        var events = JsonDocument.Parse(logoutToken.GetClaim("events").Value).RootElement;
        Assert.True(events.TryGetProperty(BackChannelLogoutConstants.EventUri, out var member));
        Assert.Equal(JsonValueKind.Object, member.ValueKind);
        Assert.InRange((logoutToken.ValidTo - logoutToken.IssuedAt).TotalSeconds, 100, 130);

        // The delivery outcome is visible on the client (admin detail).
        var detail = await WaitForDeliveryStatusAsync(alpha.Id);
        Assert.Equal(BackChannelLogoutDeliveryStatus.Delivered, detail!.BackChannelLogoutLastOutcome);
        Assert.NotNull(detail.BackChannelLogoutLastDeliveryAt);

        // The grants are gone with the session.
        await using var query = GetTenantedSession();
        Assert.Empty(await query.Query<SessionGrant>().Where(g => g.SessionId == Guid.Parse(sid)).ToListAsync(ct));
    }

    [Fact]
    public async Task RP_initiated_logout_notifies_the_other_relying_parties_but_not_the_initiator()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, userName) = await CreateUserAsync("rpinit");
        var initiator = await CreateRelyingPartyAsync("init", AccessTokenType.Jwt, logoutUri: SinkUri("init"));
        var other = await CreateRelyingPartyAsync("other", AccessTokenType.Jwt, logoutUri: SinkUri("other"));

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var initiatorTokens = await DriveAuthCodeFlowAsync(cookieClient, initiator);
        using var otherTokens = await DriveAuthCodeFlowAsync(cookieClient, other);
        var idTokenHint = initiatorTokens.RootElement.GetProperty("id_token").GetString()!;

        var endSession = await cookieClient.GetAsync(
            $"/connect/logout?id_token_hint={Uri.EscapeDataString(idTokenHint)}", ct);
        Assert.True((int)endSession.StatusCode is >= 200 and < 400, $"{endSession.StatusCode}: {await endSession.Content.ReadAsStringAsync(ct)}");
        await Factory.WaitForProjectionsAsync();

        var toOther = await Sink.WaitForAsync(other.LogoutUri!, 1);
        Assert.Single(toOther);
        await Task.Delay(500, ct);
        Assert.Empty(Sink.Deliveries.Where(d => d.Target.ToString() == initiator.LogoutUri));
    }

    [Fact]
    public async Task Force_sign_out_sends_one_user_level_logout_token_per_relying_party()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, userName) = await CreateUserAsync("force");
        var rp = await CreateRelyingPartyAsync("force", AccessTokenType.Jwt, logoutUri: SinkUri("force"));

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        var issuer = new JsonWebToken(tokens.RootElement.GetProperty("id_token").GetString()!).Issuer;

        var everywhere = await cookieClient.DeleteAsync("/api/auth/sessions", ct);
        Assert.Equal(HttpStatusCode.NoContent, everywhere.StatusCode);
        await Factory.WaitForProjectionsAsync();

        var deliveries = await Sink.WaitForAsync(rp.LogoutUri!, 1);
        await Task.Delay(500, ct);
        deliveries = await Sink.WaitForAsync(rp.LogoutUri!, 1);
        var single = Assert.Single(deliveries);

        var token = await ValidateLogoutTokenAsync(single.LogoutToken, issuer, rp.ClientId);
        Assert.Equal(user.ToString(), token.Subject);
        Assert.False(token.TryGetClaim("sid", out _), "a user-level end names no session");
    }

    [Fact]
    public async Task A_failed_delivery_is_recorded_and_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, userName) = await CreateUserAsync("retry");
        var rp = await CreateRelyingPartyAsync("retry", AccessTokenType.Jwt, logoutUri: SinkUri("retry"));
        Sink.Respond(rp.LogoutUri!, HttpStatusCode.ServiceUnavailable);

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        (await cookieClient.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        var first = await Sink.WaitForAsync(rp.LogoutUri!, 1);
        Assert.Single(first);
        var detail = await WaitForDeliveryStatusAsync(rp.Id);
        Assert.Equal("failed:http-503", detail!.BackChannelLogoutLastOutcome);

        // The pending row is scheduled for the first retry step (one minute out).
        await using (var query = GetTenantedSession())
        {
            var pending = Assert.Single(await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).ToListAsync(ct));
            Assert.Equal(1, pending.Attempts);
            Assert.InRange(pending.NextAttemptAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
        }

        // Make it due and let the per-realm retry job sweep: a second, fresh token is sent.
        await using (var mutate = GetTenantedDocumentSession())
        {
            var pending = await mutate.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).SingleAsync(ct);
            pending.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            mutate.Store(pending);
            await mutate.SaveChangesAsync(ct);
        }
        Sink.Respond(rp.LogoutUri!, HttpStatusCode.OK);
        using (var scope = CreateTenantScope())
        {
            var (attempted, delivered) = await scope.ServiceProvider.GetRequiredService<IBackChannelLogoutDeliverer>().SweepDueAsync(ct);
            Assert.Equal(1, attempted);
            Assert.Equal(1, delivered);
        }

        var second = await Sink.WaitForAsync(rp.LogoutUri!, 2);
        Assert.Equal(2, second.Count);
        Assert.NotEqual(second[0].LogoutToken, second[1].LogoutToken);
        detail = await WaitForDeliveryStatusAsync(rp.Id, expected: BackChannelLogoutDeliveryStatus.Delivered);
        Assert.Equal(BackChannelLogoutDeliveryStatus.Delivered, detail!.BackChannelLogoutLastOutcome);
        Assert.NotNull(detail.BackChannelLogoutLastDeliveryAt);
        await using (var query = GetTenantedSession())
            Assert.Empty(await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).ToListAsync(ct));
    }

    [Fact]
    public async Task Retries_are_given_up_after_the_last_schedule_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, userName) = await CreateUserAsync("giveup");
        var rp = await CreateRelyingPartyAsync("giveup", AccessTokenType.Jwt, logoutUri: SinkUri("giveup"));
        Sink.Respond(rp.LogoutUri!, HttpStatusCode.BadGateway);

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        (await cookieClient.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();
        Assert.Single(await Sink.WaitForAsync(rp.LogoutUri!, 1));

        for (var step = 0; step < BackChannelLogoutConstants.RetrySchedule.Length; step++)
        {
            await using (var mutate = GetTenantedDocumentSession())
            {
                var pending = await mutate.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).SingleAsync(ct);
                pending.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
                mutate.Store(pending);
                await mutate.SaveChangesAsync(ct);
            }
            using var scope = CreateTenantScope();
            var (attempted, delivered) = await scope.ServiceProvider.GetRequiredService<IBackChannelLogoutDeliverer>().SweepDueAsync(ct);
            Assert.Equal((1, 0), (attempted, delivered));
        }

        // One immediate attempt plus one per schedule step, then nothing is pending any more.
        Assert.Equal(1 + BackChannelLogoutConstants.RetrySchedule.Length, Sink.Deliveries.Count(d => d.Target.ToString() == rp.LogoutUri));
        await using (var query = GetTenantedSession())
            Assert.Empty(await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).ToListAsync(ct));
        var detail = await WaitForDeliveryStatusAsync(rp.Id);
        Assert.Equal("failed:http-502", detail!.BackChannelLogoutLastOutcome);
    }

    [Fact]
    public async Task Discovery_advertises_back_channel_logout()
    {
        var ct = TestContext.Current.CancellationToken;
        using var doc = JsonDocument.Parse(await Factory.CreateClient().GetStringAsync("/.well-known/openid-configuration", ct));
        Assert.True(doc.RootElement.GetProperty("backchannel_logout_supported").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("backchannel_logout_session_supported").GetBoolean());
    }

    [Theory]
    [InlineData("http://rp.example.com/logout", false)]
    [InlineData("https://rp.example.com/logout#frag", false)]
    [InlineData("https://10.0.0.5/logout", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    [InlineData("not a uri", false)]
    [InlineData("https://rp.example.com/oidc/backchannel-logout", true)]
    [InlineData("http://localhost:5000/logout", true)]
    [InlineData("http://127.0.0.1:5000/logout", true)]
    public async Task The_logout_uri_is_validated_at_registration(string uri, bool accepted)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = $"bcl-validate-{Guid.NewGuid():N}",
            ClientSecret = "secret-secret-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = "validate",
            RedirectUris = ["https://rp.example.com/cb"],
            AllowedGrantTypes = ["authorization_code"],
            BackChannelLogoutUri = uri,
        }, ct);

        Assert.Equal(accepted, !result.IsError);
        if (!accepted) Assert.Equal("OAuth.InvalidBackChannelLogoutUri", result.FirstError.Code);
    }

    // ── transport B ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Sessions_appear_in_the_change_feed_and_are_deleted_with_a_reason_on_logout()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (user, userName) = await CreateUserAsync("feed");
        var appId = Guid.CreateVersion7();
        var appSlug = $"bclfeed-{suffix}";
        var groupId = Guid.CreateVersion7();

        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Events.StartStream<App>(appId, new AppCreatedEvent(appId, appSlug, "Back-channel feed", null, [], false));
            arrange.Events.StartStream<Group>(groupId, new GroupCreatedEvent(
                groupId, $"Feed group {suffix}", null, [user], [], BoundTo: [appSlug]));
            await arrange.SaveChangesAsync(ct);
        }
        await Factory.WaitForProjectionsAsync();

        var rp = await CreateRelyingPartyAsync("feed", AccessTokenType.Jwt, logoutUri: null, appIds: [appId]);

        var enable = await Client.PutAsJsonAsync(
            $"/api/app/{ShortGuid.Encode(appId)}",
            new UpdateAppDto("Back-channel feed", null, [], new ApplicationSettingsDto
            {
                ChangeFeed = new ApplicationChangeFeedDto { Enabled = true, MinimumRetentionAgeDays = 7, MinimumEventCount = 1_000 },
            }),
            JsonOptions,
            ct);
        enable.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Anchor the fan-out subscription (SubscribeFromPresent) before the session ends.
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        var sid = new JsonWebToken(tokens.RootElement.GetProperty("id_token").GetString()!).GetClaim("sid").Value;
        await Factory.WaitForProjectionsAsync();

        var grantId = SessionGrant.IdFor(Guid.Parse(sid), rp.ClientId);
        await using (var query = GetTenantedSession())
        {
            var upsert = await query.Query<AppChangeFeedEntry>()
                .Where(x => x.AppId == appId && x.ChangeKind == "Upsert" && x.EntityKind == "session" && x.EntityId == grantId)
                .SingleAsync(ct);
            var payload = JsonDocument.Parse(upsert.PayloadJson!).RootElement;
            Assert.Equal(sid, payload.GetProperty("SessionId").GetString());
            Assert.Equal(user.ToString(), payload.GetProperty("Sub").GetString());
            Assert.Equal(rp.ClientId, payload.GetProperty("ClientId").GetString());
            Assert.Equal("browser", payload.GetProperty("Kind").GetString());
        }

        (await cookieClient.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        await using (var query = GetTenantedSession())
        {
            var deleted = await query.Query<AppChangeFeedEntry>()
                .Where(x => x.AppId == appId && x.ChangeKind == "Deleted" && x.EntityKind == "session" && x.EntityId == grantId)
                .SingleAsync(ct);
            Assert.Equal("logout", deleted.Reason);
            var tombstone = JsonDocument.Parse(deleted.PayloadJson!).RootElement;
            Assert.Equal(sid, tombstone.GetProperty("SessionId").GetString());
            Assert.Equal("logout", tombstone.GetProperty("Reason").GetString());
            Assert.Empty(await query.Query<AppChangeFeedEntityState>()
                .Where(x => x.AppId == appId && x.EntityKind == "session").ToListAsync(ct));
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed record RelyingParty(string Id, string ClientId, string Secret, string RedirectUri, string? LogoutUri);

    private IServiceScope CreateTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()
            .HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }

    private static string SinkUri(string tag) => $"https://{tag}-{Guid.NewGuid():N}.rp.example/oidc/backchannel-logout";

    private async Task<(Guid Id, string UserName)> CreateUserAsync(string tag)
    {
        var acronym = $"bcl{tag}{Guid.NewGuid():N}"[..16];
        var view = await Factory.CreateTestUserWithIdentityAsync("Backchannel", tag, acronym, $"{acronym}@bcl.example", Password);
        return (view.Id, acronym);
    }

    private async Task<RelyingParty> CreateRelyingPartyAsync(string tag, AccessTokenType tokenType, string? logoutUri, List<Guid>? appIds = null)
    {
        var clientId = $"bcl-{tag}-{Guid.NewGuid():N}"[..40];
        var secret = $"secret-{Guid.NewGuid():N}";
        var redirect = $"https://{tag}.rp.example/callback";
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [redirect],
            Scopes = ["openid", "profile", "offline_access"],
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RequireConsent = false,
            AccessTokenType = tokenType,
            BackChannelLogoutUri = logoutUri,
            AppIds = appIds is null ? [] : [.. appIds.Select(g => new ShortGuid(g).ToString())],
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
        return new RelyingParty(result.Value.Client.Id, clientId, secret, redirect, logoutUri);
    }

    private async Task<JsonDocument> DriveAuthCodeFlowAsync(HttpClient cookieClient, RelyingParty rp)
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authorizeUri = "/connect/authorize?" + string.Join("&",
        [
            "response_type=code",
            $"client_id={Uri.EscapeDataString(rp.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(rp.RedirectUri)}",
            "scope=openid%20profile%20offline_access",
            $"state={Guid.NewGuid():N}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
        ]);
        var authorize = await cookieClient.GetAsync(authorizeUri, ct);
        Assert.True((int)authorize.StatusCode is >= 300 and < 400,
            $"authorize: {(int)authorize.StatusCode} {await authorize.Content.ReadAsStringAsync(ct)}");
        var code = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]
                   ?? throw new Xunit.Sdk.XunitException($"no code in {authorize.Headers.Location}");

        var token = await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = rp.ClientId,
            ["client_secret"] = rp.Secret,
            ["redirect_uri"] = rp.RedirectUri,
            ["code_verifier"] = verifier,
        }), ct);
        var body = await token.Content.ReadAsStringAsync(ct);
        Assert.True(token.IsSuccessStatusCode, $"token: {(int)token.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> RefreshAsync(string refreshToken, RelyingParty rp)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = rp.ClientId,
            ["client_secret"] = rp.Secret,
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"refresh: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> IntrospectAsync(RelyingParty rp, string token)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["token_type_hint"] = "access_token" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{rp.ClientId}:{rp.Secret}")));
        var response = await Factory.CreateClient().SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"introspect: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    private async Task<JsonWebToken> ValidateLogoutTokenAsync(string token, string issuer, string clientId)
    {
        var ct = TestContext.Current.CancellationToken;
        var jwks = new JsonWebKeySet(await Factory.CreateClient().GetStringAsync("/.well-known/jwks", ct));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = [LogoutTokenMinter.TokenType],
            ValidateLifetime = true,
        });
        Assert.True(result.IsValid, result.Exception?.ToString());
        return (JsonWebToken)result.SecurityToken;
    }

    private async Task<OAuthClientDto?> WaitForDeliveryStatusAsync(string clientPk, string? expected = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            using var scope = Factory.Services.CreateScope();
            var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
            var dto = await admin.GetClientByIdAsync(clientPk, TestContext.Current.CancellationToken);
            if (dto?.BackChannelLogoutLastOutcome is { } outcome && (expected is null || outcome == expected)) return dto;
            if (DateTimeOffset.UtcNow > deadline) return dto;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
