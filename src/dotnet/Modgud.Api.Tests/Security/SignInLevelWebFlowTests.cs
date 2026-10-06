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
        await SeedPasskeyAsync(DefaultUser!.Id, rpId: "app-dev.example-app.test");
        var code = await IssueCodeAsync();

        var response = await PasswordlessLoginAsync(Factory.CreateDefaultClient(new CookieContainerHandler()), code);

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
        var passwordOnly = await CreateAuthenticatedClientAsync("tu", DefaultPassword);

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

    private async Task<string> IssueCodeAsync()
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
        var result = await otp.RequestNativeOtpAsync(DefaultUser!.Id, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, "RequestNativeOtpAsync failed to issue a challenge");
        var email = emailService.GetLastEmailTo(TestEmail);
        Assert.NotNull(email);
        return OtpCodeRegex().Match(email!.HtmlBody).Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PasswordlessLoginAsync(HttpClient browser, string code) =>
        browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = TestEmail, Code = code }, TestContext.Current.CancellationToken);

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
