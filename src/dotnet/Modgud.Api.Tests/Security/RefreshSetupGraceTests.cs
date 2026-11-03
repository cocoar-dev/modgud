using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.SignIn;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B8-AS01: real authorization-code/PKCE and refresh pipeline, without native
/// grants or manufactured token factors. A refresh never starts a setup grace.
/// Assertions deliberately do not print tokens, cookies, codes or TOTP keys.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RefreshSetupGraceTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string RedirectUri = "https://refresh.example.test/callback";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rig(Guid UserId, Guid AppId, string ClientId, HttpClient Browser) : IDisposable
    {
        public void Dispose() => Browser.Dispose();
    }

    private sealed record Tokens(string RefreshToken, string Acr, bool HasMfa);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 14)]
    [InlineData(true, 0)]
    [InlineData(true, 14)]
    public async Task Raised_policy_without_a_started_grace_refuses_refresh_without_starting_it(
        bool realmPolicy, int graceDays)
    {
        using var rig = await ArrangeAsync();
        var tokens = await AuthorizeAsync(rig);
        Assert.Equal(SignInAssurance.AcrSingle, tokens.Acr);
        Assert.Null((await SecurityAsync(rig))?.SecureSetupDueAt);

        await RaisePolicyAsync(rig, realmPolicy, graceDays);
        await RefuseAsync(rig, tokens.RefreshToken, strongerSignIn: true);

        // A background refresh does not silently award or start the setup window.
        Assert.Null((await SecurityAsync(rig))?.SecureSetupDueAt);
    }

    [Fact]
    public async Task An_interactive_sign_in_starts_the_grace_once_then_refresh_stops_at_its_deadline()
    {
        using var rig = await ArrangeAsync();
        var oldTokens = await AuthorizeAsync(rig);
        await RaisePolicyAsync(rig, realmPolicy: false, graceDays: 14);
        await RefuseAsync(rig, oldTokens.RefreshToken, strongerSignIn: true);

        // A fresh interactive authorization, not the rejected refresh, starts it.
        var tokens = await AuthorizeAsync(rig);
        var dueAt = (await SecurityAsync(rig))?.SecureSetupDueAt;
        Assert.NotNull(dueAt);
        Assert.True(dueAt > DateTime.UtcNow.AddDays(13));
        var renewed = await RenewAsync(rig, tokens.RefreshToken);
        Assert.Equal(SignInAssurance.AcrSingle, renewed.Acr);
        Assert.False(renewed.HasMfa);
        Assert.Equal(dueAt, (await SecurityAsync(rig))?.SecureSetupDueAt);

        // Deterministic expiry arrangement, no clock changes or weakened policy.
        var expired = DateTime.UtcNow.AddMinutes(-1);
        await ChangeSecurityAsync(rig, security => security.SecureSetupDueAt = expired);
        await RefuseAsync(rig, renewed.RefreshToken, strongerSignIn: true);
        Assert.Equal(expired, (await SecurityAsync(rig))?.SecureSetupDueAt);
    }

    [Fact]
    public async Task An_unchanged_single_policy_keeps_refreshing_without_a_setup_deadline()
    {
        using var rig = await ArrangeAsync();
        var tokens = await AuthorizeAsync(rig);
        var renewed = await RenewAsync(rig, tokens.RefreshToken);
        var again = await RenewAsync(rig, renewed.RefreshToken);
        Assert.Equal(SignInAssurance.AcrSingle, again.Acr);
        Assert.False(again.HasMfa);
        Assert.Null((await SecurityAsync(rig))?.SecureSetupDueAt);
    }

    [Fact]
    public async Task An_explicit_setup_exemption_is_not_an_unstarted_grace_or_a_new_factor()
    {
        using var rig = await ArrangeAsync();
        var tokens = await AuthorizeAsync(rig);
        await ChangeSecurityAsync(rig, security => security.TwoFactorExempt = true);
        await RaisePolicyAsync(rig, realmPolicy: false, graceDays: 0);

        var renewed = await RenewAsync(rig, tokens.RefreshToken);
        Assert.Equal(SignInAssurance.AcrSingle, renewed.Acr);
        Assert.False(renewed.HasMfa);
        Assert.Null((await SecurityAsync(rig))?.SecureSetupDueAt);
    }

    [Fact]
    public async Task A_real_password_and_totp_sign_in_still_refreshes_under_a_raised_multi_policy()
    {
        using var rig = await ArrangeAsync();
        using var setup = await rig.Browser.PostAsync("/api/account/mfa/setup", null, Ct);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("SharedKey").GetString()!;
        using var verified = await rig.Browser.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = AuthEnforcementTests.GenerateTotpForTest(key) }, Ct);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var tokens = await AuthorizeAsync(rig);
        Assert.Equal(SignInAssurance.AcrMulti, tokens.Acr);
        Assert.True(tokens.HasMfa);
        await RaisePolicyAsync(rig, realmPolicy: false, graceDays: 0);

        var renewed = await RenewAsync(rig, tokens.RefreshToken);
        Assert.Equal(SignInAssurance.AcrMulti, renewed.Acr);
        Assert.True(renewed.HasMfa);
        Assert.Null((await SecurityAsync(rig))?.SecureSetupDueAt);
    }

    [Fact]
    public async Task A_setup_grace_does_not_replace_an_available_second_factor()
    {
        using var rig = await ArrangeAsync();
        var tokens = await AuthorizeAsync(rig);
        // Isolate the factor gate from the separately tested stamp kill-switch:
        // arrange an enrolled factor without rotating the existing token's stamp.
        await ChangeSecurityAsync(rig, security =>
        {
            security.TwoFactorEnabled = true;
            security.SecureSetupDueAt = DateTime.UtcNow.AddDays(10);
        });

        await RefuseAsync(rig, tokens.RefreshToken, strongerSignIn: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_stamp_and_authorization_revocation_still_refuse_refresh(bool revokeAuthorization)
    {
        using var rig = await ArrangeAsync();
        var tokens = await AuthorizeAsync(rig);
        if (revokeAuthorization)
        {
            using var scope = Factory.Services.CreateScope();
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            await foreach (var authorization in authorizations.FindBySubjectAsync(rig.UserId.ToString(), Ct))
                Assert.True(await authorizations.TryRevokeAsync(authorization, Ct));
        }
        else
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(rig.UserId.ToString());
            Assert.NotNull(user);
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }

        await RefuseAsync(rig, tokens.RefreshToken, strongerSignIn: false);
        Assert.Null((await SecurityAsync(rig))?.SecureSetupDueAt);
    }

    private async Task<Rig> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..12];
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Refresh", "Grace", $"rg{tag}", $"rg{tag}@example.test", DefaultPassword);
        var appId = Guid.NewGuid();
        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<App>(appId, new AppCreatedEvent(
                appId, $"rg-{tag}", "Refresh grace", null, [], false));
            await session.SaveChangesAsync(Ct);
        }

        var clientId = $"refresh-grace-{tag}";
        using (var scope = Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>()
                .CreateClientAsync(new CreateOAuthClientDto
                {
                    ClientId = clientId,
                    ClientType = OAuthClientTypes.Public,
                    ConsentType = OAuthConsentTypes.Implicit,
                    DisplayName = "Refresh grace",
                    RedirectUris = [RedirectUri],
                    Scopes = ["openid", "offline_access"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    RequireConsent = false,
                    AccessTokenType = AccessTokenType.Jwt,
                    AppIds = [new ShortGuid(appId).ToString()],
                }, Ct);
            Assert.False(result.IsError);
        }
        var browser = await CreateAuthenticatedClientAsync($"rg{tag}", DefaultPassword);
        return new Rig(user.Id, appId, clientId, browser);
    }

    private async Task RaisePolicyAsync(Rig rig, bool realmPolicy, int graceDays)
    {
        if (realmPolicy)
        {
            using var scope = Factory.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
                .PatchAsync(new UpdateRealmSettingsDto
                {
                    SignIn = new UpdateSignInPolicyDto { MinimumLevel = "Multi", SetupGraceDays = graceDays },
                }, Ct);
            Assert.False(result.IsError);
            return;
        }
        await using var session = GetTenantedDocumentSession();
        session.Store(new ApplicationSettings
        {
            Id = rig.AppId,
            CreatedAt = DateTimeOffset.UtcNow,
            SignIn = new ApplicationSignInOverrides { MinimumLevel = SignInLevel.Multi, SetupGraceDays = graceDays },
        });
        await session.SaveChangesAsync(Ct);
    }

    private Task<UserSecurityData?> SecurityAsync(Rig rig) =>
        Factory.GetDocumentAsync<UserSecurityData>(rig.UserId);

    private async Task ChangeSecurityAsync(Rig rig, Action<UserSecurityData> change)
    {
        await using var session = GetTenantedDocumentSession();
        var security = await session.LoadAsync<UserSecurityData>(rig.UserId, Ct);
        Assert.NotNull(security);
        change(security);
        session.Store(security);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Tokens> AuthorizeAsync(Rig rig)
    {
        var verifier = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var authorize = await rig.Browser.GetAsync("/connect/authorize?" + string.Join("&",
            "response_type=code",
            $"client_id={Uri.EscapeDataString(rig.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            "scope=openid%20offline_access",
            $"code_challenge={challenge}",
            "code_challenge_method=S256"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var location = authorize.Headers.Location;
        Assert.True(location is { IsAbsoluteUri: true } && location.GetLeftPart(UriPartial.Path) == RedirectUri,
            "Authorization must reach only the synthetic callback, not the login/setup page.");
        var code = System.Web.HttpUtility.ParseQueryString(location!.Query)["code"];
        Assert.True(!string.IsNullOrEmpty(code), "The synthetic callback must contain an authorization code.");
        using var tokenClient = Factory.CreateClient();
        using var response = await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = rig.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code"] = code!,
            ["code_verifier"] = verifier,
        }), Ct);
        return await ReadTokensAsync(response);
    }

    private async Task<HttpResponseMessage> RefreshAsync(Rig rig, string refreshToken)
    {
        using var tokenClient = Factory.CreateClient();
        return await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = rig.ClientId,
            ["refresh_token"] = refreshToken,
        }), Ct);
    }

    private async Task<Tokens> RenewAsync(Rig rig, string refreshToken)
    {
        using var response = await RefreshAsync(rig, refreshToken);
        return await ReadTokensAsync(response);
    }

    private async Task RefuseAsync(Rig rig, string refreshToken, bool strongerSignIn)
    {
        using var response = await RefreshAsync(rig, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("access_token", out _));
        Assert.False(body.TryGetProperty("refresh_token", out _));
        if (strongerSignIn)
            Assert.Equal("The application now requires a stronger sign-in; please sign in again.",
                body.GetProperty("error_description").GetString());
    }

    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var refresh = body.GetProperty("refresh_token").GetString();
        Assert.True(!string.IsNullOrEmpty(refresh), "The response must contain a refresh token.");
        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString()!);
        return new Tokens(refresh!, jwt.GetClaim("acr").Value,
            jwt.Claims.Any(c => c.Type == "amr" && c.Value == "mfa"));
    }
}
