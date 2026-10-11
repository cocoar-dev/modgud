using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.RealmSettings;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// ADR 0025 — what a browser sign-in has to reach, end to end: the e-mail-code login no
/// longer refuses accounts for factors the page cannot use, the user's own TOTP continues
/// into a second step instead of a dead end, and the admin surface raises a single-factor
/// session instead of letting it through or locking it out.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class SignInLevelWebFlowTests : IntegrationTestBase
{
    public SignInLevelWebFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string TestEmail = "test@test.com";

    [Fact]
    public async Task An_e_mail_code_sign_in_is_not_refused_for_a_passkey_a_native_app_enrolled()
    {
        // The reported case: the native app enrolled a passkey under its own RP ID. The
        // login page cannot use it, and the user never switched on a second factor.
        await PatchSignInAsync(new UpdateSignInPolicyDto { EmailCode = true });
        var user = await PlainUserAsync();
        await SeedPasskeyAsync(user.Id, rpId: "app-dev.example-app.test");
        var code = await IssueCodeAsync(user.Id, PlainEmail);

        var response = await PasswordlessLoginAsync(Factory.CreateDefaultClient(new CookieContainerHandler()), code, PlainEmail);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains("Login successful", body);
    }

    [Fact]
    public async Task The_users_own_totp_continues_an_e_mail_code_sign_in_instead_of_refusing_it()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { EmailCode = true });
        await EnableTotpAsync();
        var code = await IssueCodeAsync();
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var first = await PasswordlessLoginAsync(browser, code);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(firstBody.GetProperty("RequiresMfa").GetBoolean());
        Assert.Equal(["totp"], firstBody.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));

        // Not signed in yet — only the partial sign-in exists.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/account/me", TestContext.Current.CancellationToken)).StatusCode);

        var second = await browser.PostAsJsonAsync("/api/account/mfa/login",
            new { Code = await TotpCodeAsync() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await browser.GetAsync("/api/account/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_single_factor_session_is_raised_before_it_reaches_the_administration()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        // A passkey for the realm's own domain: usable on the login page.
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var passwordOnly = await SignedInForAnAppAsync();

        var admin = await passwordOnly.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);
        var adminBody = await admin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.True(adminBody.GetProperty("RequiresStepUp").GetBoolean());

        // The portal is not the administration: the same session still reads its profile.
        Assert.Equal(HttpStatusCode.OK,
            (await passwordOnly.GetAsync("/api/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);

        var stepUp = await passwordOnly.PostAsJsonAsync("/api/account/step-up",
            new { ReturnUrl = "/admin/realm-settings" }, TestContext.Current.CancellationToken);
        var stepUpBody = await stepUp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(stepUpBody.GetProperty("RequiresMfa").GetBoolean());
        Assert.Contains("passkey", stepUpBody.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task An_administrator_signing_in_to_Modgud_is_asked_for_the_second_factor_at_once()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordLoginAsync(browser, "tu", returnUrl: null);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(body.GetProperty("RequiresMfa").GetBoolean());
        Assert.Contains("passkey", body.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));
        // No session at the portal's level in between: nothing to show a dashboard with.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/account/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task An_administrator_signing_in_for_an_app_follows_the_apps_rules()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordLoginAsync(browser, "tu", AppReturnUrl);

        Assert.Contains("Login successful", await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK,
            (await browser.GetAsync("/api/account/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_user_who_is_not_an_administrator_signs_in_to_Modgud_with_one_factor()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        var user = await PlainUserAsync();
        await SeedPasskeyAsync(user.Id, rpId: null);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordLoginAsync(browser, "pu", returnUrl: null);

        Assert.Contains("Login successful", await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_administrator_is_not_asked_when_the_administration_needs_one_factor()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Single" });
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordLoginAsync(browser, "tu", returnUrl: null);

        Assert.Contains("Login successful", await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_administrator_without_a_second_factor_learns_of_the_setup_at_sign_in()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", SetupGraceDays = 14 });
        await ForgetSetupGraceAsync();
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordLoginAsync(browser, "tu", returnUrl: null);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // Signed in, inside the grace: the login page offers the setup and "later".
        Assert.True(body.GetProperty("RequiresSecureSetup").GetBoolean());
        Assert.True(body.GetProperty("GracePeriod").GetBoolean());
        Assert.Equal(HttpStatusCode.OK,
            (await browser.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task An_e_mail_code_sign_in_to_Modgud_asks_an_administrator_for_the_second_factor_too()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", EmailCode = true });
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordlessLoginAsync(browser, await IssueCodeAsync());
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.True(body.GetProperty("RequiresMfa").GetBoolean());
        Assert.Contains("passkey", body.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task Me_tells_the_page_that_the_administration_would_ask_before_any_request_goes_there()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var passwordOnly = await SignedInForAnAppAsync();

        var me = await passwordOnly.GetFromJsonAsync<JsonElement>("/api/account/me", TestContext.Current.CancellationToken);

        // The same answer the administration gives, so the page can ask first.
        Assert.True(me.GetProperty("AdministrationNeedsStepUp").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await passwordOnly.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Me_reports_an_open_administration_during_the_setup_grace_without_starting_it()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", SetupGraceDays = 14 });
        await ForgetSetupGraceAsync();
        var passwordOnly = await SignedInForAnAppAsync();

        var me = await passwordOnly.GetFromJsonAsync<JsonElement>("/api/account/me", TestContext.Current.CancellationToken);

        Assert.False(me.GetProperty("AdministrationNeedsStepUp").GetBoolean());
        // Reading /me is not a visit to the administration: it starts no grace.
        Assert.Null((await Factory.GetDocumentAsync<UserSecurityData>(DefaultUser!.Id))?.SecureSetupDueAt);
        Assert.Equal(HttpStatusCode.OK,
            (await passwordOnly.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Me_reports_a_closed_administration_when_the_setup_grace_is_zero_days()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", SetupGraceDays = 0 });
        await ForgetSetupGraceAsync();
        var passwordOnly = await SignedInForAnAppAsync();

        var me = await passwordOnly.GetFromJsonAsync<JsonElement>("/api/account/me", TestContext.Current.CancellationToken);

        // No grace has started yet; the first visit would start one that is over at once.
        Assert.True(me.GetProperty("AdministrationNeedsStepUp").GetBoolean());
        Assert.Null((await Factory.GetDocumentAsync<UserSecurityData>(DefaultUser!.Id))?.SecureSetupDueAt);
        var admin = await passwordOnly.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.True((await admin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("RequiresSecureSetup").GetBoolean());
    }

    [Fact]
    public async Task Me_reports_an_open_administration_when_the_realm_asks_it_for_one_factor()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Single" });
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: null);
        var passwordOnly = await CreateAuthenticatedClientAsync("tu", DefaultPassword);

        var me = await passwordOnly.GetFromJsonAsync<JsonElement>("/api/account/me", TestContext.Current.CancellationToken);

        Assert.False(me.GetProperty("AdministrationNeedsStepUp").GetBoolean());
    }

    [Fact]
    public async Task A_user_without_a_second_factor_uses_the_administration_during_the_setup_grace()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", SetupGraceDays = 14 });
        var passwordOnly = await CreateAuthenticatedClientAsync("tu", DefaultPassword);

        var admin = await passwordOnly.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var security = await Factory.GetDocumentAsync<UserSecurityData>(DefaultUser!.Id);
        Assert.NotNull(security?.SecureSetupDueAt);
        Assert.True(security!.SecureSetupDueAt > DateTime.UtcNow.AddDays(13));
    }

    [Fact]
    public async Task App_info_reports_the_sign_in_methods_instead_of_the_retired_level()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { Password = false, EmailCode = true });

        var response = await Factory.CreateClient().GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.False(body.TryGetProperty("AuthenticationMinimumLevel", out _));
        var signIn = body.GetProperty("SignIn");
        Assert.False(signIn.GetProperty("Password").GetBoolean());
        Assert.True(signIn.GetProperty("EmailCode").GetBoolean());
    }

    // ── Helpers ──

    private async Task PatchSignInAsync(UpdateSignInPolicyDto signIn)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { [TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId },
            };
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var result = await settings.PatchAsync(new UpdateRealmSettingsDto { SignIn = signIn }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    private async Task SeedPasskeyAsync(Guid userId, string? rpId)
    {
        await using var session = GetTenantedDocumentSession();
        session.Store(new StoredPasskeyCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            PublicKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64),
            UserHandle = userId.ToByteArray(),
            DisplayName = "Passkey",
            RpId = rpId,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private const string PlainEmail = "plain@test.com";

    /// <summary>A pending authorization as the login page's return URL: the sign-in is for
    /// a client, not for Modgud's own UI. No App is bound to it, so the realm's policy applies.</summary>
    private const string AppReturnUrl = "/connect/authorize?client_id=some-app&response_type=code";

    /// <summary>A user without any permission in Modgud's own Apps (user name <c>pu</c>).</summary>
    private Task<Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView> PlainUserAsync() =>
        Factory.CreateTestUserWithIdentityAsync(
            firstname: "Plain", lastname: "User", acronym: "PU", email: PlainEmail, password: DefaultPassword);

    private static Task<HttpResponseMessage> PasswordLoginAsync(HttpClient browser, string userName, string? returnUrl) =>
        browser.PostAsJsonAsync("/api/account/login",
            new { UserName = userName, Password = DefaultPassword, ReturnUrl = returnUrl }, TestContext.Current.CancellationToken);

    /// <summary>The default user (a realm administrator) with a password-only session, as a
    /// sign-in for an App leaves it — a sign-in to Modgud itself would be raised at once.</summary>
    private async Task<HttpClient> SignedInForAnAppAsync()
    {
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var login = await PasswordLoginAsync(browser, "tu", AppReturnUrl);
        Assert.Contains("Login successful", await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return browser;
    }

    /// <summary>The test setup signed the default user in to Modgud, which started their
    /// setup grace; forget it, as for a user who never owed a second factor.</summary>
    private async Task ForgetSetupGraceAsync()
    {
        await using var session = GetTenantedDocumentSession();
        var security = await session.LoadAsync<UserSecurityData>(DefaultUser!.Id, TestContext.Current.CancellationToken);
        if (security is null) return;
        security.SecureSetupDueAt = null;
        session.Store(security);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task<string> IssueCodeAsync() => IssueCodeAsync(DefaultUser!.Id, TestEmail);

    private async Task<string> IssueCodeAsync(Guid userId, string address)
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { [TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId },
            };
        var otp = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();
        var result = await otp.RequestNativeOtpAsync(userId, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, "RequestNativeOtpAsync failed to issue a challenge");
        var email = emailService.GetLastEmailTo(address);
        Assert.NotNull(email);
        return OtpCodeRegex().Match(email!.HtmlBody).Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PasswordlessLoginAsync(HttpClient browser, string code, string address = TestEmail) =>
        browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = address, Code = code }, TestContext.Current.CancellationToken);

    private async Task EnableTotpAsync()
    {
        await Client.PostAsync("/api/account/mfa/setup", null, TestContext.Current.CancellationToken);
        var resp = await Client.PostAsJsonAsync("/api/account/mfa/verify",
            new { Code = await TotpCodeAsync() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<string> TotpCodeAsync()
    {
        var securityData = await Factory.GetDocumentAsync<UserSecurityData>(DefaultUser!.Id);
        Assert.NotNull(securityData?.AuthenticatorKey);
        return AuthEnforcementTests.GenerateTotpForTest(securityData!.AuthenticatorKey!);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(\d{6})\b")]
    private static partial System.Text.RegularExpressions.Regex OtpCodeRegex();
}
