using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
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
using Modgud.Authentication.Identity;
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
/// ADR-0011 Phase 4 — issuer anchoring + branded login. A request on an
/// Application subdomain must report the tenant canonical issuer (discovery,
/// token iss) so strict clients don't see a cross-host mismatch, while a plain
/// realm host keeps its per-host issuer (zero behaviour change). Also: /api/app-info
/// returns App branding on the subdomain. The host-swap rule itself is unit-pinned
/// in CanonicalIssuerTests.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class IssuerAnchoringFlowTests : IntegrationTestBase
{
    public IssuerAnchoringFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Discovery_On_App_Subdomain_Reports_Canonical_Issuer()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("iss-disco-app");
        await MapApplicationDomainsAsync(("iss-disco.localhost", app.Id));
        var primaryDomain = await SystemPrimaryDomainAsync();

        // On the App subdomain → issuer host anchors to the tenant PrimaryDomain.
        var sub = await GetIssuerAsync("iss-disco.localhost");
        Assert.Equal(primaryDomain, new Uri(sub).Host);
        Assert.NotEqual("iss-disco.localhost", new Uri(sub).Host);

        // On a plain tenant host → issuer is that host (unchanged behaviour).
        var plain = await GetIssuerAsync(primaryDomain);
        Assert.Equal(primaryDomain, new Uri(plain).Host);
    }

    [Fact]
    public async Task Token_Minted_On_App_Subdomain_Carries_Canonical_Issuer()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("iss-tok-app");
        var sa = await CreateServiceAccountAsync("iss-tok-sa");
        await CreateRealmWideClientCredentialsClientAsync("iss-tok-client", sa);
        await MapApplicationDomainsAsync(("iss-tok.localhost", app.Id));
        var primaryDomain = await SystemPrimaryDomainAsync();

        var token = await MintAccessTokenAsync("iss-tok.localhost", "iss-tok-client");
        var iss = new JwtSecurityTokenHandler().ReadJwtToken(token).Issuer;

        Assert.Equal(primaryDomain, new Uri(iss).Host);
        Assert.NotEqual("iss-tok.localhost", new Uri(iss).Host);
    }

    [Fact]
    public async Task AppInfo_On_App_Subdomain_Returns_App_Branding()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("iss-brand-app");
        await StoreApplicationSettingsAsync(new ApplicationSettings
        {
            Id = app.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            Branding = new Modgud.Domain.Realms.BrandingSettings { ProductName = "AcmeList" },
            PageTheme = new ApplicationPageTheme
            {
                AccentColor = "#10b981",
                ButtonRadiusPx = 999,
            },
        });
        await MapApplicationDomainsAsync(("iss-brand.localhost", app.Id));

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/app-info");
        req.Headers.Host = "iss-brand.localhost";
        var resp = await Client.SendAsync(req, ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("AcmeList", json.GetProperty("Branding").GetProperty("ProductName").GetString());
        Assert.Equal("#10b981", json.GetProperty("PageTheme").GetProperty("AccentColor").GetString());
        Assert.Equal(999, json.GetProperty("PageTheme").GetProperty("ButtonRadiusPx").GetInt32());
    }

    [Fact]
    public async Task AppInfo_Publishes_Resolved_Registration_Field_Policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("iss-regfields-app");
        await StoreApplicationSettingsAsync(new ApplicationSettings
        {
            Id = app.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            // Enterprise App in a tenant whose realm default is lenient: require
            // names, hide the separate username.
            RegistrationFields = new ApplicationRegistrationFieldsOverrides
            {
                Username = FieldRequirement.Off,
                Firstname = FieldRequirement.Required,
                Lastname = FieldRequirement.Required,
            },
        });
        await MapApplicationDomainsAsync(("iss-regfields.localhost", app.Id));

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/app-info");
        req.Headers.Host = "iss-regfields.localhost";
        var resp = await Client.SendAsync(req, ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rf = json.GetProperty("RegistrationFields");
        Assert.Equal("Required", rf.GetProperty("Email").GetString());     // always the anchor
        Assert.Equal("Off", rf.GetProperty("Username").GetString());
        Assert.Equal("Required", rf.GetProperty("Firstname").GetString());
        Assert.Equal("Required", rf.GetProperty("Lastname").GetString());
    }

    [Fact]
    public async Task AppInfo_Defaults_Registration_Fields_To_Optional_When_Unconfigured()
    {
        var ct = TestContext.Current.CancellationToken;

        // Plain tenant host, no Application, no realm policy configured → the lenient
        // default (all three Optional), i.e. zero behaviour change.
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/app-info");
        var resp = await Client.SendAsync(req, ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rf = json.GetProperty("RegistrationFields");
        Assert.Equal("Optional", rf.GetProperty("Username").GetString());
        Assert.Equal("Optional", rf.GetProperty("Firstname").GetString());
        Assert.Equal("Optional", rf.GetProperty("Lastname").GetString());
    }

    [Fact]
    public async Task UserInfo_On_App_Subdomain_Accepts_A_Subdomain_Minted_Token()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "test@test.com"; // the seeded DefaultUser
        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync("iss-ui-app");
        await CreateOtpClientAsync("iss-ui-client", app.Id);
        await MapApplicationDomainsAsync(("iss-ui.localhost", app.Id));

        var code = await IssueOtpViaServiceAsync(DefaultUser!.Id, email);
        var token = await MintOtpTokenAsync("iss-ui.localhost", "iss-ui-client", email, code);

        // The token's iss is the tenant canonical origin (minted on the subdomain);
        // userinfo on that SAME subdomain must accept it — proving the validation
        // side is anchored to match the minting side.
        var req = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        req.Headers.Host = "iss-ui.localhost";
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await Client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        Assert.True(resp.IsSuccessStatusCode, $"userinfo rejected the subdomain token ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(DefaultUser.Id.ToString(), json.RootElement.GetProperty("sub").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> GetIssuerAsync(string host)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        req.Headers.Host = host;
        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("issuer").GetString()!;
    }

    private async Task<string> MintAccessTokenAsync(string host, string clientId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = $"{clientId}-secret",
            }),
        };
        req.Headers.Host = host;
        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/token failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<string> SystemPrimaryDomainAsync()
    {
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var session = globalStore.QuerySession();
        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == "system", TestContext.Current.CancellationToken);
        Assert.NotNull(realm);
        Assert.False(string.IsNullOrEmpty(realm!.PrimaryDomain), "system realm must have a PrimaryDomain");
        return realm.PrimaryDomain;
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

    private async Task StoreApplicationSettingsAsync(ApplicationSettings settings)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(settings);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> CreateServiceAccountAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = name, Purpose = "adr-0011-phase4" }, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dto.GetProperty("Id").GetString()!;
    }

    private async Task CreateRealmWideClientCredentialsClientAsync(string clientId, string serviceAccountId)
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
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Scopes = [],
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [], // realm-wide → passes the first-signal-consistency gate on any host
            LinkedServiceAccountId = serviceAccountId,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task EnableRealmNativeGrantsAsync()
    {
        using var scope = NewSystemScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
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

    private async Task<string> IssueOtpViaServiceAsync(Guid userId, string email)
    {
        var ct = TestContext.Current.CancellationToken;
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        using var scope = NewSystemScope();
        var otp = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();
        var result = await otp.RequestNativeOtpAsync(userId, ct);
        Assert.False(result.IsError, "RequestNativeOtpAsync failed to issue a challenge");
        var msg = emailService.GetLastEmailTo(email);
        Assert.NotNull(msg);
        return Regex.Match(msg!.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;
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
        Assert.True(resp.IsSuccessStatusCode, $"/connect/token (otp) failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private IServiceScope NewSystemScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
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
