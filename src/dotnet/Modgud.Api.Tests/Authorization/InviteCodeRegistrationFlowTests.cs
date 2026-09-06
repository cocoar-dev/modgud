using System.Net;
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
using Modgud.Authentication.SelfRegistration;
using Modgud.Authentication.SelfRegistration.Domain;
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
/// ADR-0012 — invite-code-gated passwordless self-registration (the fourth
/// <see cref="SelfRegPosture.InviteCode"/> posture). Under this posture an unknown
/// email at the native OTP-request endpoint becomes a passwordless user ONLY when
/// the request carries a valid, unused, unexpired, app-matching code. This pins the
/// gates from the ADR: code-required, atomic single-use (concurrent-redeem),
/// anti-enumeration parity, D11 (confirmed user + code = plain login, code
/// untouched), and the admin-permission mint path.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class InviteCodeRegistrationFlowTests : IntegrationTestBase
{
    public InviteCodeRegistrationFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Host = "adr12-invite.localhost";

    [Fact]
    public async Task InviteCode_UnknownEmail_WithValidCode_Opens_The_Pipeline_And_Creates_The_User_On_Redeem()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "adr12-invitee@example.test";
        var app = await SetUpInviteAppAsync("adr12-valid");
        var code = await MintCodeAsync(app.Id, boundEmail: null);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var resp = await RequestNativeOtpAsync(Host, email, code);
        resp.EnsureSuccessStatusCode();

        var msg = emailService.GetLastEmailTo(email);
        Assert.NotNull(msg); // a registration code was issued → the gate opened
        var otp = Regex.Match(msg!.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;
        // ADR 0018: the consumed code opened the pipeline, but no user exists before the proof.
        Assert.Null(await QuerySystemUserByEmailAsync(email));

        var token = await MintOtpTokenAsync(Host, "adr12-valid-client", email, otp);
        Assert.False(string.IsNullOrEmpty(token));

        var user = await QuerySystemUserByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);

        // The code is now consumed (single-use) and links the created user.
        var consumed = await LoadCodeByPlaintextAsync(app.Id, code);
        Assert.NotNull(consumed);
        Assert.True(consumed!.IsUsed);
        Assert.Equal(user.Id, consumed.UsedByUserId);
    }

    [Fact]
    public async Task InviteCode_UnknownEmail_WithoutCode_CreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "adr12-nocode@example.test";
        await SetUpInviteAppAsync("adr12-nocode");

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var resp = await RequestNativeOtpAsync(Host, email, inviteCode: null);
        resp.EnsureSuccessStatusCode();

        Assert.Null(emailService.GetLastEmailTo(email)); // no code → no email
        Assert.Null(await QuerySystemUserByEmailAsync(email)); // no user created
    }

    [Fact]
    public async Task InviteCode_FailurePaths_Are_ByteIdentical_To_NoAccount()
    {
        await SetUpInviteAppAsync("adr12-antienum");

        // Three failure shapes that must NOT be distinguishable from one another:
        // no code, a wrong code, and (implicitly) "no account". All return the same
        // status + body so the endpoint is not an existence/validity oracle.
        var noCode = await RequestNativeOtpAsync(Host, "adr12-ae-a@example.test", inviteCode: null);
        var badCode = await RequestNativeOtpAsync(Host, "adr12-ae-b@example.test", inviteCode: "totally-bogus-code");
        var expiredShape = await RequestNativeOtpAsync(Host, "adr12-ae-c@example.test", inviteCode: "another-bogus");

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.OK, noCode.StatusCode);
        Assert.Equal(noCode.StatusCode, badCode.StatusCode);
        Assert.Equal(noCode.StatusCode, expiredShape.StatusCode);

        var b1 = await noCode.Content.ReadAsStringAsync(ct);
        var b2 = await badCode.Content.ReadAsStringAsync(ct);
        var b3 = await expiredShape.Content.ReadAsStringAsync(ct);
        Assert.Equal(b1, b2);
        Assert.Equal(b1, b3);
    }

    [Fact]
    public async Task InviteCode_BearerCode_Is_SingleUse_Under_Concurrent_Redeem()
    {
        var app = await SetUpInviteAppAsync("adr12-race");
        var code = await MintCodeAsync(app.Id, boundEmail: null); // bearer → reusable shape

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        const string emailA = "adr12-race-a@example.test";
        const string emailB = "adr12-race-b@example.test";

        // Two different emails redeem the SAME bearer code concurrently. The atomic
        // optimistic-concurrency consume must let exactly one through.
        var a = RequestNativeOtpAsync(Host, emailA, code);
        var b = RequestNativeOtpAsync(Host, emailB, code);
        await Task.WhenAll(a, b);
        (await a).EnsureSuccessStatusCode();
        (await b).EnsureSuccessStatusCode();

        // ADR 0018: no user exists before a proof either way — the single-use
        // guarantee now shows as exactly ONE registration code being issued.
        Assert.Null(await QuerySystemUserByEmailAsync(emailA));
        Assert.Null(await QuerySystemUserByEmailAsync(emailB));
        var issued = new[] { emailService.GetLastEmailTo(emailA), emailService.GetLastEmailTo(emailB) }
            .Count(m => m is not null);
        Assert.Equal(1, issued); // single-use held — only one sign-up got a code
    }

    [Fact]
    public async Task InviteCode_ConfirmedUser_With_Code_Is_Plain_Login_Code_Untouched()
    {
        // D11 — an existing confirmed user presenting a code just logs in; the code
        // is ignored and NOT consumed (stays valid until expiry/revoke).
        var app = await SetUpInviteAppAsync("adr12-d11");
        var code = await MintCodeAsync(app.Id, boundEmail: null);

        var email = "adr12-d11-existing@example.test";
        await SeedConfirmedPasswordlessUserAsync(email);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var resp = await RequestNativeOtpAsync(Host, email, code);
        resp.EnsureSuccessStatusCode();

        // A login OTP was sent (the user signs in) ...
        Assert.NotNull(emailService.GetLastEmailTo(email));
        // ... and the code is still unused.
        var stored = await LoadCodeByPlaintextAsync(app.Id, code);
        Assert.NotNull(stored);
        Assert.False(stored!.IsUsed);
    }

    [Fact]
    public async Task InviteCode_Posture_RoundTrips_Through_App_Resource()
    {
        // Gate 1 — the new posture value survives a unified App update → GET on the
        // single App resource (settings carried inline; sparse, zero-migration).
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("adr12-posture");
        var appShort = new ShortGuid(app.Id).ToString();

        var put = await Client.PutAsJsonAsync(
            $"/api/app/{appShort}",
            new
            {
                DisplayName = "adr12-posture",
                Description = (string?)null,
                Permissions = Array.Empty<object>(),
                Settings = new { SelfRegistration = new { Posture = "InviteCode" } },
            }, ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await Client.GetFromJsonAsync<JsonElement>($"/api/app/{appShort}", JsonOptions, ct);
        Assert.Equal("InviteCode",
            got.GetProperty("Settings").GetProperty("SelfRegistration").GetProperty("Posture").GetString());
    }

    [Fact]
    public async Task MintEndpoint_AdminCookie_Mints_Codes()
    {
        // The admin-UI path: a realm admin (cookie auth) holds the invite-code:write
        // permission via the realm:admin bypass, so the dual-auth filter lets the
        // mint endpoint through.
        var ct = TestContext.Current.CancellationToken;
        var app = await SetUpInviteAppAsync("adr12-mint");
        var appShort = new ShortGuid(app.Id).ToString();

        var resp = await Client.PostAsJsonAsync(
            $"/api/app/{appShort}/invite-codes",
            new { Count = 3, BoundEmail = (string?)null, ExpiresInDays = (int?)7 }, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        var codes = body.GetProperty("Codes").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(3, codes.Count);
        Assert.All(codes, c => Assert.False(string.IsNullOrWhiteSpace(c)));

        // List endpoint returns the freshly minted codes (metadata only).
        var list = await Client.GetFromJsonAsync<JsonElement>($"/api/app/{appShort}/invite-codes", JsonOptions, ct);
        Assert.True(list.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task ListAll_Admin_Returns_Codes_Across_Apps()
    {
        // The realm-wide admin overview lists EVERY app's codes (the grid loads
        // this once and filters client-side). Permission-gated, not dual-auth.
        var ct = TestContext.Current.CancellationToken;
        var appA = await CreateAppAsync("adr12-all-a");
        var appB = await CreateAppAsync("adr12-all-b");
        var appAShort = new ShortGuid(appA.Id).ToString();
        var appBShort = new ShortGuid(appB.Id).ToString();

        await Client.PostAsJsonAsync($"/api/app/{appAShort}/invite-codes", new { Count = 2 }, ct);
        await Client.PostAsJsonAsync($"/api/app/{appBShort}/invite-codes", new { Count = 1 }, ct);

        var all = await Client.GetFromJsonAsync<JsonElement>("/api/admin/invite-codes", JsonOptions, ct);
        var appIds = all.EnumerateArray().Select(e => e.GetProperty("AppId").GetString()).ToList();
        Assert.Contains(appAShort, appIds);
        Assert.Contains(appBShort, appIds);
        Assert.True(appIds.Count >= 3);
    }

    [Fact]
    public async Task Prune_Removes_Used_And_Expired_Codes_Keeps_Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("adr12-prune");

        // One open code (should survive) + one already-expired code (should go).
        using (var scope = Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
                .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new RegistrationInviteCode
            {
                Id = Guid.NewGuid(), AppId = app.Id, CodeHash = "open-hash",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), CreatedAt = DateTimeOffset.UtcNow,
                CreatedBySubject = "test",
            });
            session.Store(new RegistrationInviteCode
            {
                Id = Guid.NewGuid(), AppId = app.Id, CodeHash = "expired-hash",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1), CreatedAt = DateTimeOffset.UtcNow.AddDays(-15),
                CreatedBySubject = "test",
            });
            await session.SaveChangesAsync(ct);

            var svc = scope.ServiceProvider.GetRequiredService<IRegistrationInviteService>();
            var pruned = await svc.PruneAsync(ct);
            Assert.Equal(1, pruned); // only the expired one

            var remaining = await svc.ListAsync(app.Id, ct);
            Assert.Single(remaining);
            Assert.Equal("open-hash", remaining[0].CodeHash);
        }
    }

    [Fact]
    public async Task MintEndpoint_M2M_Scope_Mints_For_Bound_App_And_Rejects_CrossApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var appA = await CreateAppAsync("adr12-m2m-a");
        var appB = await CreateAppAsync("adr12-m2m-b");
        var appAShort = new ShortGuid(appA.Id).ToString();
        var appBShort = new ShortGuid(appB.Id).ToString();

        // App-bound invite:write scope + a client_credentials client bound to App A.
        await CreateScopeAsync("invite:write", appA.Id);
        var sa = await CreateServiceAccountAsync("adr12-m2m-sa");
        await CreateClientCredentialsClientAsync("adr12-m2m-client", sa, [appAShort], ["invite:write"]);

        var token = await GetClientCredentialsTokenAsync("adr12-m2m-client", "invite:write");
        Assert.False(string.IsNullOrEmpty(token));

        // Mint for App A (the scope's app) → 200.
        var ok = await PostInviteCodesWithBearerAsync(appAShort, token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        Assert.Equal(2, body.GetProperty("Codes").GetArrayLength());

        // Same token minting for App B (NOT the scope's/client's app) → 403.
        var cross = await PostInviteCodesWithBearerAsync(appBShort, token);
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task CreateScopeAsync(string name, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauthAdmin.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            AppId = new ShortGuid(appId).ToString(),
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateScopeAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task<string> CreateServiceAccountAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = name, Purpose = "adr-0012" }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return dto.GetProperty("Id").GetString()!;
    }

    private async Task CreateClientCredentialsClientAsync(
        string clientId, string serviceAccountId, List<string> appIds, List<string> scopes)
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
            Scopes = scopes,
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = appIds,
            LinkedServiceAccountId = serviceAccountId,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task<string> GetClientCredentialsTokenAsync(string clientId, string scope)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = $"{clientId}-secret",
            ["scope"] = scope,
        }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/token (client_credentials) failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> PostInviteCodesWithBearerAsync(string appShort, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/app/{appShort}/invite-codes")
        {
            Content = JsonContent.Create(new { Count = 2, BoundEmail = (string?)null, ExpiresInDays = (int?)7 }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private async Task<App> SetUpInviteAppAsync(string slug)
    {
        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync(slug);
        await SeedAppPostureAsync(app.Id, SelfRegPosture.InviteCode);
        await CreateOtpClientAsync($"{slug}-client", app.Id);
        await MapApplicationDomainsAsync((Host, app.Id));
        return app;
    }

    private Task<HttpResponseMessage> RequestNativeOtpAsync(string host, string email, string? inviteCode)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
        {
            Content = JsonContent.Create(new { Email = email, InviteCode = inviteCode }),
        };
        req.Headers.Host = host;
        return Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private async Task<string> MintCodeAsync(Guid appId, string? boundEmail)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var svc = scope.ServiceProvider.GetRequiredService<IRegistrationInviteService>();
        var codes = await svc.MintAsync(appId, boundEmail, expiresInDays: null,
            createdBySubject: "test", count: 1, TestContext.Current.CancellationToken);
        return codes[0];
    }

    private async Task<RegistrationInviteCode?> LoadCodeByPlaintextAsync(Guid appId, string plaintext)
    {
        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)));
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.Query<RegistrationInviteCode>()
            .FirstOrDefaultAsync(c => c.AppId == appId && c.CodeHash == hash,
                TestContext.Current.CancellationToken);
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

    private async Task SeedConfirmedPasswordlessUserAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var user = new ApplicationUser(email, email)
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            EmailConfirmed = true,
        };
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
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
