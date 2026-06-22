using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Microsoft.AspNetCore.Http;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 5 — native passwordless REGISTRATION (the amZettel driver). On an
/// App subdomain with the JIT posture (the Application default), an unknown email
/// at the native OTP-request endpoint creates a passwordless user and emails a
/// registration code; redeeming it at /connect/token mints tokens AND confirms the
/// mailbox. The routing matrix (incl. posture gating) is unit-pinned in
/// NativeOtpDecisionTests; this is the end-to-end wiring.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeRegistrationFlowTests : IntegrationTestBase
{
    public NativeRegistrationFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Jit_Registration_Creates_Passwordless_User_And_Confirms_On_Redeem()
    {
        var ct = TestContext.Current.CancellationToken;
        var newEmail = "jit-phase5-newuser@example.test"; // not seeded → unknown

        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync("p5-jit-app"); // no ApplicationSettings doc → posture defaults JitOnOtp
        await CreateOtpClientAsync("p5-jit-client", app.Id);
        await MapApplicationDomainsAsync(("p5-jit.localhost", app.Id));

        // 1) Unknown email on the App subdomain → JIT create + registration code.
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var reqResp = await RequestNativeOtpAsync("p5-jit.localhost", newEmail);
        reqResp.EnsureSuccessStatusCode();
        var msg = emailService.GetLastEmailTo(newEmail);
        Assert.NotNull(msg); // JIT issued a registration code to the unknown email
        var code = Regex.Match(msg!.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(code));

        // 2) Redeem at /connect/token → tokens minted (the registration completes).
        var token = await MintOtpTokenAsync("p5-jit.localhost", "p5-jit-client", newEmail, code);
        Assert.False(string.IsNullOrEmpty(token));

        // 3) The user now exists, passwordless, and EmailConfirmed flipped true.
        var user = await QuerySystemUserByEmailAsync(newEmail);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.True(string.IsNullOrEmpty(user.PasswordHash));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> RequestNativeOtpAsync(string host, string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
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
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant() && !u.IsDeleted,
                TestContext.Current.CancellationToken);
    }

    private async Task EnableRealmNativeGrantsAsync()
    {
        var scope = Factory.Services.CreateScope();
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
