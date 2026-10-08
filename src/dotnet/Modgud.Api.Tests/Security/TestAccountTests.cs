using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
using Modgud.Authentication.TestAccounts;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// ADR 0026 — test accounts: an ordinary account with a marker, a fixed e-mail code, the
/// <c>modgud.test_account</c> claim, and the barrier around Modgud's own administration.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TestAccountTests : IntegrationTestBase
{
    public TestAccountTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string TestAccountEmail = "ta@test.com";
    private const string FixedCode = "246810";
    private const string NativeClient = "ta-native";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Permission ──

    [Fact]
    public async Task Marking_needs_its_own_permission()
    {
        var target = await CreateTestAccountUserAsync();
        await Factory.CreateTestUserWithIdentityAsync("Plain", "User", "pu", "pu@test.com", DefaultPassword);
        var plain = await CreateAuthenticatedClientAsync("pu", DefaultPassword);

        var response = await plain.PutAsJsonAsync($"/api/admin/users/{Id(target)}/test-account", new { IsTestAccount = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var session = GetTenantedSession();
        var modgud = await session.Query<App>().SingleAsync(a => a.Slug == AppSlugs.Modgud, Ct);
        Assert.Contains(modgud.Permissions, p => p.Resource == "user" && p.Action == "test-account");
    }

    // ── Fixed e-mail code ──

    [Fact]
    public async Task The_fixed_code_signs_in_natively_without_a_mail_and_every_token_says_test_account()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        await SetCodeAsync(target, FixedCode);
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync();

        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>();
        mail.Clear();
        var request = await Factory.CreateClient().PostAsJsonAsync("/api/account/native/otp/request", new { Email = TestAccountEmail }, Ct);
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        Assert.Null(mail.GetLastEmailTo(TestAccountEmail));

        var token = await NativeOtpAsync(FixedCode);
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync(Ct));
        using var json = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
        var accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        var idToken = json.RootElement.GetProperty("id_token").GetString()!;

        Assert.Equal("true", Claim(accessToken));
        Assert.Equal("true", Claim(idToken));

        var userinfoClient = Factory.CreateClient();
        userinfoClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var userinfo = await userinfoClient.GetFromJsonAsync<JsonElement>("/connect/userinfo", Ct);
        Assert.True(userinfo.GetProperty(TestAccountClaims.Type).GetBoolean());
    }

    [Fact]
    public async Task An_ordinary_account_carries_no_test_account_claim()
    {
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync();
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>();
        mail.Clear();
        await Factory.CreateClient().PostAsJsonAsync("/api/account/native/otp/request", new { Email = "test@test.com" }, Ct);
        var code = System.Text.RegularExpressions.Regex.Match(mail.GetLastEmailTo("test@test.com")!.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;

        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:cocoar:otp",
            ["client_id"] = NativeClient,
            ["client_secret"] = $"{NativeClient}-secret",
            ["username"] = "test@test.com",
            ["otp_code"] = code,
            ["scope"] = "openid",
        });
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync(Ct));
        using var json = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
        Assert.Null(Claim(json.RootElement.GetProperty("access_token").GetString()!));
        Assert.Null(Claim(json.RootElement.GetProperty("id_token").GetString()!));
    }

    [Fact]
    public async Task The_fixed_code_signs_in_on_the_login_page_and_creates_no_sent_code()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        await SetCodeAsync(target, FixedCode);
        await EnableNativeGrantsAsync(); // the realm then offers the e-mail code

        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var request = await browser.PostAsJsonAsync("/api/account/passwordless-otp/request", new { Email = TestAccountEmail }, Ct);
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        await using (var session = GetTenantedSession())
            Assert.Null(await session.LoadAsync<EmailOtpChallenge>(target.Id, Ct));

        var login = await browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = TestAccountEmail, Code = FixedCode }, Ct);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/account/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_slow_down_instead_of_locking_out()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        await SetCodeAsync(target, FixedCode);
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync();

        for (var i = 0; i < TestAccountService.FreeAttempts + 1; i++)
            Assert.False((await NativeOtpAsync("000000")).IsSuccessStatusCode);

        // Inside the delay even the right code is not checked.
        Assert.False((await NativeOtpAsync(FixedCode)).IsSuccessStatusCode);

        await using (var session = GetTenantedDocumentSession())
        {
            var row = await session.LoadAsync<TestAccountEmailCode>(target.Id, Ct);
            Assert.NotNull(row!.RetryNotBefore);
            row.RetryNotBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
            session.Store(row);
            await session.SaveChangesAsync(Ct);
        }

        // After the delay the right code works: no lockout.
        Assert.True((await NativeOtpAsync(FixedCode)).IsSuccessStatusCode);
        await using var check = GetTenantedSession();
        var after = await check.LoadAsync<TestAccountEmailCode>(target.Id, Ct);
        Assert.Equal(0, after!.FailedAttempts);
        Assert.NotNull(after.LastUsedAt);
        Assert.Equal(NativeClient, after.LastUsedClientId);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public async Task The_fixed_code_is_exactly_six_digits(string code)
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);

        var response = await Client.PutAsJsonAsync($"/api/admin/users/{Id(target)}/test-account/fixed-email-code", new { Code = code }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TestAccount.CodeFormat", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Only_a_test_account_gets_a_fixed_code()
    {
        var target = await CreateTestAccountUserAsync();

        var response = await Client.PutAsJsonAsync($"/api/admin/users/{Id(target)}/test-account/fixed-email-code", new { Code = FixedCode }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TestAccount.NotMarked", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_expired_fixed_code_no_longer_signs_in()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        await SetCodeAsync(target, FixedCode, DateTimeOffset.UtcNow.AddDays(1));
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync();
        await using (var session = GetTenantedDocumentSession())
        {
            var row = await session.LoadAsync<TestAccountEmailCode>(target.Id, Ct);
            row!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            session.Store(row);
            await session.SaveChangesAsync(Ct);
        }

        Assert.False((await NativeOtpAsync(FixedCode)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Unmarking_deletes_the_fixed_code_and_the_claim()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        await SetCodeAsync(target, FixedCode);

        await MarkAsync(target, false);

        await using var session = GetTenantedSession();
        Assert.Null(await session.LoadAsync<TestAccountEmailCode>(target.Id, Ct));
        var status = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/users/{Id(target)}/test-account", Ct);
        Assert.False(status.GetProperty("IsTestAccount").GetBoolean());
        Assert.False(status.GetProperty("HasFixedEmailCode").GetBoolean());
    }

    // ── Administration barrier and group exclusion ──

    [Fact]
    public async Task An_account_in_an_administration_group_cannot_be_marked()
    {
        var target = await CreateTestAccountUserAsync();
        var adminRole = await Factory.CreateTestRoleAsync("Ta Admin", isRealmAdmin: true);
        await Factory.CreateTestGroupAsync("Ta Admins", [target.Id], [adminRole.Id], boundTo: ["*"]);

        var response = await Client.PutAsJsonAsync($"/api/admin/users/{Id(target)}/test-account", new { IsTestAccount = true }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Ta Admins", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_test_account_never_resolves_an_administration_permission_however_it_got_there()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        var userManager = await Factory.CreateTestRoleAsync("Ta Users", [("user", "write")]);
        var appRole = await Factory.CreateTestRoleAsync("Ta App Role");
        // Written past every write-time guard, straight into the event stream.
        await Factory.CreateTestGroupAsync("Ta Sneaky", [target.Id], [userManager.Id]);

        using var scope = TenantScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        Assert.Empty(await permissions.GetUserPermissionsAsync(target.Id, AppSlugs.Modgud, Ct));
        Assert.DoesNotContain(await permissions.GetUserGroupsAsync(target.Id, Ct), g => g.Name == "Ta Sneaky");
        Assert.False(await permissions.HasPermissionAsync(target.Id, AppSlugs.Modgud, "user:write", Ct));
        _ = appRole;
    }

    [Fact]
    public async Task A_group_excluding_test_accounts_refuses_them_and_its_script_leaves_them_out()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);

        var manual = await Client.PostAsJsonAsync("/api/group", new
        {
            Name = "Ta App Admins",
            MemberIds = new[] { Id(target) },
            RoleIds = Array.Empty<string>(),
            ExcludeTestAccounts = true,
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, manual.StatusCode);
        Assert.Contains("Group.TestAccountExcluded", await manual.Content.ReadAsStringAsync(Ct));

        var closedAuto = await CreateAutoGroupAsync("Ta Closed Auto", excludeTestAccounts: true);
        var openAuto = await CreateAutoGroupAsync("Ta Open Auto", excludeTestAccounts: false);

        await using var session = GetTenantedSession();
        Assert.DoesNotContain(target.Id, (await session.LoadAsync<Group>(closedAuto, Ct))!.MemberIds);
        Assert.Contains(target.Id, (await session.LoadAsync<Group>(openAuto, Ct))!.MemberIds);
        Assert.Contains(DefaultUser!.Id, (await session.LoadAsync<Group>(closedAuto, Ct))!.MemberIds);
    }

    [Fact]
    public async Task Ticking_the_exclusion_is_refused_while_a_test_account_is_a_direct_member()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        var created = await Client.PostAsJsonAsync("/api/group", new
        {
            Name = "Ta Team", MemberIds = new[] { Id(target) }, RoleIds = Array.Empty<string>(),
        }, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var group = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);

        var update = await Client.PutAsJsonAsync($"/api/group/{group.GetProperty("Id").GetString()}", new
        {
            Name = "Ta Team", MemberIds = new[] { Id(target) }, RoleIds = Array.Empty<string>(), ExcludeTestAccounts = true,
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Contains("ta", await update.Content.ReadAsStringAsync(Ct));
    }

    // ── Manifest ──

    [Fact]
    public async Task The_marker_is_exported_and_the_code_never()
    {
        var target = await CreateTestAccountUserAsync();
        await MarkAsync(target, true);
        await SetCodeAsync(target, FixedCode);

        var export = await Client.GetStringAsync("/api/admin/realm-config/export", Ct);

        using var json = JsonDocument.Parse(export);
        var user = json.RootElement.GetProperty("Users").EnumerateArray()
            .Single(u => u.GetProperty("Email").GetString() == TestAccountEmail);
        Assert.True(user.GetProperty("IsTestAccount").GetBoolean());
        Assert.DoesNotContain(FixedCode, export);
    }

    // ── Helpers ──

    private IServiceScope TenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { [TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId } };
        return scope;
    }

    private Task<Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView> CreateTestAccountUserAsync() =>
        Factory.CreateTestUserWithIdentityAsync("Test", "Account", "ta", TestAccountEmail, DefaultPassword);

    private static string Id(Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView user) =>
        new ShortGuid(user.Id).ToString();

    private async Task MarkAsync(Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView user, bool marked)
    {
        var response = await Client.PutAsJsonAsync($"/api/admin/users/{Id(user)}/test-account", new { IsTestAccount = marked }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task SetCodeAsync(Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView user, string code, DateTimeOffset? expiresAt = null)
    {
        var response = await Client.PutAsJsonAsync($"/api/admin/users/{Id(user)}/test-account/fixed-email-code",
            new { Code = code, ExpiresAt = expiresAt }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task EnableNativeGrantsAsync()
    {
        using var scope = TenantScope();
        var result = await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
            .PatchAsync(new UpdateRealmSettingsDto { NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true } }, Ct);
        Assert.False(result.IsError);
    }

    private async Task SeedNativeClientAsync()
    {
        var appId = Guid.NewGuid();
        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<App>(appId, new AppCreatedEvent(
                Id: appId, Slug: "ta-app", DisplayName: "Ta App", Description: null, Permissions: [], IsSystem: false));
            await session.SaveChangesAsync(Ct);
        }
        using var scope = TenantScope();
        var result = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = NativeClient,
            ClientSecret = $"{NativeClient}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = NativeClient,
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "email", "profile", "offline_access"],
            AllowedGrantTypes = ["urn:cocoar:otp", "refresh_token"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [new ShortGuid(appId).ToString()],
        }, Ct);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
    }

    private Task<HttpResponseMessage> NativeOtpAsync(string code) => PostTokenAsync(new Dictionary<string, string>
    {
        ["grant_type"] = "urn:cocoar:otp",
        ["client_id"] = NativeClient,
        ["client_secret"] = $"{NativeClient}-secret",
        ["username"] = TestAccountEmail,
        ["otp_code"] = code,
        ["scope"] = "openid email profile",
    });

    private Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form) =>
        Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(form), Ct);

    private static string? Claim(string jwt)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        return token.Payload.TryGetValue(TestAccountClaims.Type, out var value) ? value?.ToString()?.ToLowerInvariant() : null;
    }

    private async Task<Guid> CreateAutoGroupAsync(string name, bool excludeTestAccounts)
    {
        var response = await Client.PostAsJsonAsync("/api/group", new
        {
            Name = name,
            MemberIds = Array.Empty<string>(),
            RoleIds = Array.Empty<string>(),
            MembershipMode = "Auto",
            MembershipScript = "(p) => Type.Is(p, 'person') && p.IsActive",
            ExcludeTestAccounts = excludeTestAccounts,
        }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new ShortGuid(body.GetProperty("Id").GetString()!).Guid;
    }
}
