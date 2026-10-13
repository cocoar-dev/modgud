using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// ADR 0025, amendment 3 — a user whose own second factor is gone (a passkey on a broken
/// device) gets back in with the password and an e-mail code, and removes the dead factor
/// with the same proof. A password that was just reset through the mailbox does not count
/// for it until the realm's wait is over.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class EmailFallbackSecondFactorTests : IntegrationTestBase
{
    public EmailFallbackSecondFactorTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string TestEmail = "test@test.com";

    [Fact]
    public async Task S15_an_administrator_whose_passkey_is_gone_signs_in_with_password_and_e_mail_code_and_removes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        var passkeyId = await SeedPasskeyAsync(DefaultUser!.Id);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        // The user never switched the e-mail code on; it is offered beside the passkey.
        var login = await PasswordLoginAsync(browser, "tu", DefaultPassword, returnUrl: null);
        var methods = await MfaMethodsAsync(login);
        Assert.Contains("passkey", methods);
        Assert.Contains("email", methods);

        await ConfirmWithEmailCodeAsync(browser, TestEmail);

        // Signed in at the administration's level …
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/admin/realm-settings", ct)).StatusCode);
        // … and the same proof removes the factor that is gone.
        var removed = await browser.DeleteAsync($"/api/account/passkey/{passkeyId}", ct);
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(ct));
        Assert.Null(await Factory.GetDocumentAsync<StoredPasskeyCredential>(passkeyId));
    }

    [Fact]
    public async Task S17_a_password_session_confirms_an_account_change_with_a_fresh_e_mail_code()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" });
        var passkeyId = await SeedPasskeyAsync(DefaultUser!.Id);
        // Signed in for an App: a password-only session, the passkey never used.
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var login = await PasswordLoginAsync(browser, "tu", DefaultPassword, AppReturnUrl);
        Assert.Contains("Login successful", await login.Content.ReadAsStringAsync(ct));

        var refused = await browser.DeleteAsync($"/api/account/passkey/{passkeyId}", ct);
        var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(refusedBody.GetProperty("RequiresReauthentication").GetBoolean());

        var stepUp = await browser.PostAsJsonAsync("/api/account/step-up", new { Reauthenticate = true }, ct);
        Assert.Contains("email", await MfaMethodsAsync(stepUp));
        await ConfirmWithEmailCodeAsync(browser, TestEmail);

        var removed = await browser.DeleteAsync($"/api/account/passkey/{passkeyId}", ct);
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task S16_a_password_reset_by_e_mail_does_not_open_the_e_mail_fallback_until_the_wait_is_over()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", EmailFallbackWaitHours = 24 });
        var admin = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Reset", lastname: "Admin", acronym: "RA", email: "reset-admin@test.com",
            password: DefaultPassword, isRealmAdmin: true);
        await SeedPasskeyAsync(admin.Id);

        // Whoever holds the mailbox sets a new password …
        const string newPassword = "NewPass4567!";
        string token;
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            token = await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(admin.Id.ToString()))!);
        }
        var reset = await Factory.CreateClient().PostAsJsonAsync("/api/account/reset-password",
            new { UserId = admin.Id.ToString(), Token = token, NewPassword = newPassword }, ct);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(ct));
        // The owner is told, so a reset they did not ask for does not go unnoticed during the wait.
        Assert.NotNull(Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo("reset-admin@test.com"));

        // … and is still asked for the passkey alone: password and code would be one proof.
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var login = await PasswordLoginAsync(browser, "ra", newPassword, returnUrl: null);
        Assert.Equal(["passkey"], await MfaMethodsAsync(login));
        var request = await browser.PostAsync("/api/account/email-otp/login/request", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, request.StatusCode);
        // A code from the e-mail-code sign-in — the same mailbox — is no way around the wait.
        var foreignCode = await IssueSignInCodeAsync(admin.Id, "reset-admin@test.com");
        var smuggled = await browser.PostAsJsonAsync("/api/account/email-otp/login", new { Code = foreignCode }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, smuggled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/account/me", ct)).StatusCode);

        // A realm without a wait offers it at once.
        await PatchSignInAsync(new UpdateSignInPolicyDto { EmailFallbackWaitHours = 0 });
        var later = await PasswordLoginAsync(Factory.CreateDefaultClient(new CookieContainerHandler()), "ra", newPassword, returnUrl: null);
        Assert.Contains("email", await MfaMethodsAsync(later));
    }

    [Fact]
    public async Task A_realm_that_does_not_offer_the_e_mail_code_after_a_password_has_no_e_mail_fallback()
    {
        await PatchSignInAsync(new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi", EmailAfterPassword = false });
        await SeedPasskeyAsync(DefaultUser!.Id);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await PasswordLoginAsync(browser, "tu", DefaultPassword, returnUrl: null);

        Assert.Equal(["passkey"], await MfaMethodsAsync(login));
        var request = await browser.PostAsync("/api/account/email-otp/login/request", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, request.StatusCode);
    }

    // ── Helpers ──

    /// <summary>A pending authorization as the login page's return URL: the sign-in is for
    /// a client, not for Modgud's own UI. No App is bound to it, so the realm's policy applies.</summary>
    private const string AppReturnUrl = "/connect/authorize?client_id=some-app&response_type=code";

    private static Task<HttpResponseMessage> PasswordLoginAsync(HttpClient browser, string userName, string password, string? returnUrl) =>
        browser.PostAsJsonAsync("/api/account/login",
            new { UserName = userName, Password = password, ReturnUrl = returnUrl }, TestContext.Current.CancellationToken);

    private static async Task<List<string?>> MfaMethodsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.True(body.TryGetProperty("RequiresMfa", out var requiresMfa) && requiresMfa.GetBoolean(), body.ToString());
        return body.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()).ToList();
    }

    /// <summary>The second step of the browser: ask for the code, read it from the mail, send it.</summary>
    private async Task ConfirmWithEmailCodeAsync(HttpClient browser, string address)
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>();
        mail.Clear();

        var request = await browser.PostAsync("/api/account/email-otp/login/request", null, ct);
        Assert.True(request.IsSuccessStatusCode, await request.Content.ReadAsStringAsync(ct));
        var sent = mail.GetLastEmailTo(address);
        Assert.NotNull(sent);
        var code = OtpCodeRegex().Match(sent!.HtmlBody).Groups[1].Value;

        var confirm = await browser.PostAsJsonAsync("/api/account/email-otp/login", new { Code = code }, ct);
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A code as the e-mail-code sign-in issues it (the first factor's path).</summary>
    private async Task<string> IssueSignInCodeAsync(Guid userId, string address)
    {
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>();
        mail.Clear();
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { [TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId },
            };
        var otp = scope.ServiceProvider.GetRequiredService<Modgud.Authentication.Identity.IEmailOtpService>();
        var issued = await otp.RequestNativeOtpAsync(userId, TestContext.Current.CancellationToken);
        Assert.False(issued.IsError, "RequestNativeOtpAsync failed to issue a challenge");
        return OtpCodeRegex().Match(mail.GetLastEmailTo(address)!.HtmlBody).Groups[1].Value;
    }

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

    /// <summary>A passkey for the realm's own domain: usable on the login page, and an
    /// account factor on the profile.</summary>
    private async Task<Guid> SeedPasskeyAsync(Guid userId)
    {
        var id = Guid.NewGuid();
        await using var session = GetTenantedDocumentSession();
        session.Store(new StoredPasskeyCredential
        {
            Id = id,
            UserId = userId,
            CredentialId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            PublicKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64),
            UserHandle = userId.ToByteArray(),
            DisplayName = "Passkey",
            RpId = null,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(\d{6})\b")]
    private static partial System.Text.RegularExpressions.Regex OtpCodeRegex();
}
