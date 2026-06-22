using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 follow-up — explicit native passwordless REGISTRATION (the
/// <see cref="SelfRegPosture.ExplicitEndpoint"/> posture). On an App subdomain
/// whose posture is ExplicitEndpoint, <c>POST /api/account/native/register</c>
/// creates a passwordless user for an unknown email and emails a registration
/// code; redeeming it at <c>/connect/token</c> mints tokens AND confirms the
/// mailbox — while the OTP-request endpoint stays strict (known users only,
/// pinned by NativeOtpDecisionTests). An App on any other posture gets nothing
/// from the register endpoint.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeExplicitRegistrationFlowTests : IntegrationTestBase
{
    public NativeExplicitRegistrationFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ExplicitPosture_Register_Creates_Passwordless_User_And_Confirms_On_Redeem()
    {
        var ct = TestContext.Current.CancellationToken;
        var newEmail = "explicit-reg-newuser@example.test"; // not seeded → unknown

        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync("p-explicit-app");
        await SeedAppPostureAsync(app.Id, SelfRegPosture.ExplicitEndpoint);
        await CreateOtpClientAsync("p-explicit-client", app.Id);
        await MapApplicationDomainsAsync(("p-explicit.localhost", app.Id));

        // 1) Unknown email at the explicit register endpoint → create + reg code.
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var resp = await PostAsync("/api/account/native/register", "p-explicit.localhost", newEmail);
        resp.EnsureSuccessStatusCode();
        var msg = emailService.GetLastEmailTo(newEmail);
        Assert.NotNull(msg);
        var code = Regex.Match(msg!.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(code));

        // 2) Redeem at /connect/token → tokens minted (the registration completes).
        var token = await MintOtpTokenAsync("p-explicit.localhost", "p-explicit-client", newEmail, code);
        Assert.False(string.IsNullOrEmpty(token));

        // 3) The user now exists, passwordless, and EmailConfirmed flipped true.
        var user = await QuerySystemUserByEmailAsync(newEmail);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.True(string.IsNullOrEmpty(user.PasswordHash));
    }

    [Fact]
    public async Task JitPosture_Register_Endpoint_Creates_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "explicit-guard-jit@example.test";

        await EnableRealmNativeGrantsAsync();
        // No ApplicationSettings doc → posture defaults to JitOnOtp. The explicit
        // register endpoint must do nothing under JIT (sign-up flows through the
        // OTP-request endpoint instead).
        var app = await CreateAppAsync("p-explicit-guard-app");
        await MapApplicationDomainsAsync(("p-explicit-guard.localhost", app.Id));

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var resp = await PostAsync("/api/account/native/register", "p-explicit-guard.localhost", email);
        resp.EnsureSuccessStatusCode(); // uniform response either way

        Assert.Null(emailService.GetLastEmailTo(email));
        Assert.Null(await QuerySystemUserByEmailAsync(email));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostAsync(string url, string host, string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { Email = email }),
        };
        req.Headers.Host = host;
        return Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private async Task<string> MintOtpTokenAsync(string host, string clientId, string email, string code)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = CocoarGrantTypes.Otp,
                ["client_id"] = clientId,
                ["client_secret"] = $"{clientId}-secret",
                ["username"] = email,
                ["otp_code"] = code,
                ["scope"] = "openid email profile offline_access",
            }),
        };
        req.Headers.Host = host;
        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/token (otp registration) failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<ApplicationUser?> QuerySystemUserByEmailAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant() && !u.IsDeleted,
                TestContext.Current.CancellationToken);
    }

    private async Task EnableRealmNativeGrantsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task SeedAppPostureAsync(Guid appId, SelfRegPosture posture)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApplicationSettings
        {
            Id = appId,
            CreatedAt = DateTimeOffset.UtcNow,
            SelfRegistration = new ApplicationSelfRegistration { Posture = posture },
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task CreateOtpClientAsync(string clientId, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"{clientId}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "email", "profile", "offline_access"],
            AllowedGrantTypes = [CocoarGrantTypes.Otp, "refresh_token"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [new ShortGuid(appId).ToString()],
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task MapApplicationDomainsAsync(params (string Host, Guid AppId)[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", ct);
            Assert.NotNull(systemRealm);
            foreach (var (host, appId) in entries)
                systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm!);
            await session.SaveChangesAsync(ct);
        }

        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
