using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.SignIn;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// The scenarios of docs/concepts/sign-in-levels.md ("Scenarios"), one test each, named by
/// the scenario's number. Sessions are forged with exactly the factors (and times) a
/// scenario names, through the app's own principal factory, session row and ticket format.
///
/// Covered elsewhere: S1 natively — CocoarNativeGrantFlowTests.Otp_Grant_MintsTokens_ShortLifetime_NoCookie
/// (which is S8 too: a native sign-in sets no cookie); S7 — PasskeyRelatedOriginsTests
/// (no related-origin capability → the realm's RP ID); S9 natively and S11 —
/// CocoarNativeGrantFlowTests.Otp_Grant_TwoFactorUser_*; S10 natively —
/// Otp_Grant_App_That_Ignores_The_Users_Own_Factor_Does_Not_Ask_For_Totp; S13 —
/// Modgud.Tests.Unit SpaShellTests; S50 — Modgud.Tests.Unit SignInRequirementEvaluationTests.
/// Not automated: S52 (a refresh token from before factors were recorded cannot be minted
/// by the current release; the refresh check skips tokens without factors).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SignInScenarioTests : IntegrationTestBase
{
    public SignInScenarioTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string TestEmail = "test@test.com";
    private const string RedirectUri = "https://app.example/callback";
    private const string ForeignAppRpId = "app-dev.example-app.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Now = TimeSpan.Zero;
    private static readonly TimeSpan MinuteAgo = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HalfHourAgo = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HourAgo = TimeSpan.FromHours(1);

    // ── Sign-in and single sign-on ──

    [Fact]
    public async Task S1_an_e_mail_code_is_the_whole_sign_in_for_a_single_target()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { MinimumLevel = "Single", EmailCode = true });
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = TestEmail, Code = await IssueCodeAsync() }, Ct);

        var body = await login.Content.ReadAsStringAsync(Ct);
        Assert.True(login.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains("Login successful", body);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/sessions", Ct)).StatusCode);
    }

    [Fact]
    public async Task S2_a_passkey_session_reaches_a_single_app_without_a_prompt()
    {
        var appB = await CreateAppClientAsync(SignInLevel.Single);
        var browser = await SessionAsync((SignInMethods.Passkey, MinuteAgo));

        Assert.StartsWith(RedirectUri, await AuthorizeLocationAsync(browser, appB));
    }

    [Fact]
    public async Task S3_a_passkey_session_reaches_a_multi_app_without_a_prompt()
    {
        var appB = await CreateAppClientAsync(SignInLevel.Multi);
        var browser = await SessionAsync((SignInMethods.Passkey, MinuteAgo));

        Assert.StartsWith(RedirectUri, await AuthorizeLocationAsync(browser, appB));
    }

    [Fact]
    public async Task S4_an_e_mail_code_session_is_asked_only_for_the_missing_totp()
    {
        await EnableTotpAsync();
        var appB = await CreateAppClientAsync(SignInLevel.Multi);
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        var location = await AuthorizeLocationAsync(browser, appB);
        Assert.StartsWith("/login?stepup=1&redirect=", location);

        var stepUp = await StepUpAsync(browser, Uri.UnescapeDataString(location!["/login?stepup=1&redirect=".Length..]));
        Assert.True(stepUp.GetProperty("RequiresMfa").GetBoolean());
        Assert.Equal(["totp"], stepUp.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task S5_without_a_second_factor_a_multi_app_is_blocked_after_the_grace_but_the_account_is_not()
    {
        await ExpireSetupGraceAsync();
        var appB = await CreateAppClientAsync(SignInLevel.Multi);
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        var location = await AuthorizeLocationAsync(browser, appB);
        Assert.StartsWith("/login?stepup=1&redirect=", location);
        var stepUp = await StepUpAsync(browser, Uri.UnescapeDataString(location!["/login?stepup=1&redirect=".Length..]));
        Assert.True(stepUp.GetProperty("RequiresSecureSetup").GetBoolean());
        Assert.False(stepUp.GetProperty("GracePeriod").GetBoolean());

        // The portal runs under the realm's floor; the profile and the deletion stay reachable.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/sessions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/deletion-status", Ct)).StatusCode);
    }

    [Fact]
    public async Task S6_a_passkey_proven_on_the_login_page_counts_for_every_app_whatever_its_rp_id()
    {
        // The session records "passkey" — not the RP ID it was bound to. A passkey of App A
        // used through related origins raises the session for App B like any other.
        await SeedPasskeyAsync(ForeignAppRpId);
        var appB = await CreateAppClientAsync(SignInLevel.Multi);
        var browser = await SessionAsync((SignInMethods.Passkey, MinuteAgo));

        Assert.StartsWith(RedirectUri, await AuthorizeLocationAsync(browser, appB));
    }

    [Fact]
    public async Task S9_the_users_own_totp_is_asked_after_the_first_factor_on_the_web()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { EmailCode = true });
        await EnableTotpAsync();
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = TestEmail, Code = await IssueCodeAsync() }, Ct);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(body.GetProperty("RequiresMfa").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/account/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task S10_an_ignore_app_does_not_ask_the_users_totp_but_the_portal_does()
    {
        await EnableTotpAsync();
        var appA = await CreateAppClientAsync(SignInLevel.Single, totp: false, ownFactor: OwnFactorNotOffered.Ignore);
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        Assert.StartsWith(RedirectUri, await AuthorizeLocationAsync(browser, appA));

        var portal = await browser.GetAsync("/api/auth/sessions", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, portal.StatusCode);
        Assert.True((await portal.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("RequiresStepUp").GetBoolean());
    }

    [Fact]
    public async Task S12_the_administration_asks_an_e_mail_code_session_for_its_second_factor()
    {
        await EnableTotpAsync();
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        var admin = await browser.GetAsync("/api/admin/realm-settings", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.True((await admin.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("RequiresStepUp").GetBoolean());
    }

    // ── The account ──

    [Fact]
    public async Task S20_a_user_without_account_factor_deletes_their_account_right_after_signing_in()
    {
        await RemovePasswordAsync();
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        var response = await browser.PostAsJsonAsync("/api/auth/delete-account", new { Reason = "S20" }, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task S21_an_older_sign_in_is_asked_for_a_fresh_e_mail_code_before_the_deletion()
    {
        await RemovePasswordAsync();
        var browser = await SessionAsync((SignInMethods.Email, HourAgo));

        var refused = await browser.PostAsJsonAsync("/api/auth/delete-account", new { Reason = "S21" }, Ct);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(body.GetProperty("RequiresReauthentication").GetBoolean());
        Assert.Equal(["email"], body.GetProperty("Methods").EnumerateArray().Select(m => m.GetString()));

        var stepUp = await StepUpAsync(browser, "/profile", reauthenticate: true);
        Assert.True(stepUp.GetProperty("RequiresFirstFactor").GetBoolean());

        // The fresh code is a sign-in of the same user; it unions into the session.
        await RealmPolicyAsync(new UpdateSignInPolicyDto { EmailCode = true });
        var relogin = await browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = TestEmail, Code = await IssueCodeAsync() }, Ct);
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);

        var accepted = await browser.PostAsJsonAsync("/api/auth/delete-account", new { Reason = "S21" }, Ct);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task S22_and_S27_a_foreign_app_passkey_is_removed_without_a_prompt_and_a_notice_is_mailed()
    {
        var passkeyId = await SeedPasskeyAsync(ForeignAppRpId);
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));
        Mail.Clear();

        var response = await browser.DeleteAsync($"/api/account/passkey/{passkeyId}", Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var notice = Mail.GetLastEmailTo(TestEmail);
        Assert.NotNull(notice);
        Assert.Contains("Passkey", notice!.HtmlBody);
    }

    [Fact]
    public async Task S23_a_totp_user_is_asked_for_totp_again_after_the_window()
    {
        await EnableTotpAsync();
        var passkeyId = await SeedPasskeyAsync(rpId: null);
        var browser = await SessionAsync((SignInMethods.Password, HalfHourAgo), (SignInMethods.Totp, HalfHourAgo));

        var remove = await browser.DeleteAsync($"/api/account/passkey/{passkeyId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        var methods = (await remove.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("Methods")
            .EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("totp", methods);

        var password = await browser.PostAsJsonAsync("/api/account/change-password",
            new { CurrentPassword = DefaultPassword, NewPassword = "NewPass12345!" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, password.StatusCode);

        var stepUp = await StepUpAsync(browser, "/profile", reauthenticate: true);
        Assert.True(stepUp.GetProperty("RequiresMfa").GetBoolean());

        var second = await browser.PostAsJsonAsync("/api/account/mfa/login", new { Code = await TotpCodeAsync() }, Ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var retried = await browser.DeleteAsync($"/api/account/passkey/{passkeyId}", Ct);
        Assert.True(retried.IsSuccessStatusCode, await retried.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task S24_an_e_mail_code_session_of_a_totp_user_cannot_change_the_account()
    {
        await EnableTotpAsync();
        var browser = await SessionAsync((SignInMethods.Email, Now));

        var response = await browser.PostAsync("/api/account/mfa/disable", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task S25_the_first_second_factor_needs_only_a_recent_sign_in()
    {
        var browser = await SessionAsync((SignInMethods.Password, MinuteAgo));

        var response = await browser.PostAsync("/api/account/mfa/setup", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task S26_a_further_factor_needs_the_existing_one()
    {
        await EnableTotpAsync();
        var browser = await SessionAsync((SignInMethods.Password, MinuteAgo));

        var response = await browser.PostAsync("/api/account/passkey/register-options", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task S28_the_deletion_is_reachable_after_the_setup_grace()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { MinimumLevel = "Multi", EmailCode = true });
        await ExpireSetupGraceAsync();
        var browser = await SessionAsync((SignInMethods.Password, MinuteAgo));

        var portal = await browser.GetAsync("/api/auth/sessions", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, portal.StatusCode);

        var delete = await browser.PostAsJsonAsync("/api/auth/delete-account", new { Reason = "S28" }, Ct);
        Assert.True(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync(Ct));
    }

    // ── Somebody else has one of the user's factors ──

    [Fact]
    public async Task S30_a_stolen_password_does_not_sign_in_a_totp_user()
    {
        await EnableTotpAsync();
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await browser.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = DefaultPassword }, Ct);

        var body = await login.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"RequiresMfa\":true", body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/account/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task S31_the_mailbox_alone_does_not_sign_in_a_totp_user()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { EmailCode = true });
        await EnableTotpAsync();
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());

        var login = await browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = TestEmail, Code = await IssueCodeAsync() }, Ct);

        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("RequiresMfa").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/account/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task S32_password_and_e_mail_second_factor_fall_together_with_the_mailbox()
    {
        // A documented limit, pinned so it is never mistaken for protection: the e-mail
        // second factor is no account factor, so a fresh sign-in with password + e-mail
        // code — both obtainable from the mailbox — may change the account.
        await EnableEmailSecondFactorAsync();
        var browser = await SessionAsync((SignInMethods.Password, MinuteAgo), (SignInMethods.Email, MinuteAgo));

        var response = await browser.PostAsync("/api/account/email-otp/disable", null, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task S33_without_any_second_factor_the_mailbox_is_the_account()
    {
        await RemovePasswordAsync();
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/api/account/mfa/setup", null, Ct)).StatusCode);
    }

    // ── Policy rules ──

    [Fact]
    public async Task S40_an_app_cannot_go_below_the_realms_minimum()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { MinimumLevel = "Multi" });
        var app = await CreateAppAsync("s40");

        var result = await PatchAppSignInAsync(app.Id, new ApplicationSignInDto { MinimumLevel = "Single" });

        Assert.True(result.IsError);
        Assert.Equal("SignIn.BelowRealmMinimum", result.FirstError.Code);
    }

    [Fact]
    public async Task S41_an_app_cannot_offer_a_method_the_realm_does_not()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { EmailCode = false });
        var app = await CreateAppAsync("s41");

        var result = await PatchAppSignInAsync(app.Id, new ApplicationSignInDto { EmailCode = true });

        Assert.True(result.IsError);
        Assert.Equal("SignIn.MethodNotOfferedByRealm", result.FirstError.Code);
    }

    [Fact]
    public async Task S42_an_app_policy_saved_below_the_floor_runs_under_the_floor()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { MinimumLevel = "Multi" });
        var clientId = await CreateAppClientAsync(SignInLevel.Single);

        var returnUrl = Uri.EscapeDataString($"/connect/authorize?client_id={clientId}&response_type=code");
        var info = await Factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/app-info?returnUrl={returnUrl}", Ct);

        Assert.Equal("Multi", info.GetProperty("SignIn").GetProperty("MinimumLevel").GetString());
    }

    [Fact]
    public async Task S43_a_low_floor_opens_the_profile_while_the_administration_asks_for_more()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { MinimumLevel = "Single", AdministrationMinimumLevel = "Multi" });
        // A passkey for the realm: usable to raise the session, and — a stored passkey never
        // demands anything — no reason for the profile to ask for more.
        await SeedPasskeyAsync(rpId: null);
        var browser = await SessionAsync((SignInMethods.Email, MinuteAgo));

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/sessions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/admin/realm-settings", Ct)).StatusCode);
    }

    // ── Upgrading ──

    [Fact]
    public async Task S51_a_browser_session_from_before_factors_were_recorded_ends_once()
    {
        var legacy = await SessionAsync(recorded: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync("/api/account/me", Ct)).StatusCode);

        // A session from 0.15.0, which recorded factors but not the marker, is not affected.
        var withFactors = await SessionAsync(recorded: false, (SignInMethods.Password, MinuteAgo));
        Assert.Equal(HttpStatusCode.OK, (await withFactors.GetAsync("/api/account/me", Ct)).StatusCode);
    }

    // ── Helpers ──

    private InMemoryEmailService Mail => Factory.Services.GetRequiredService<InMemoryEmailService>();

    private IServiceScope TenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { [TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId } };
        return scope;
    }

    private async Task RealmPolicyAsync(UpdateSignInPolicyDto signIn)
    {
        using var scope = TenantScope();
        var result = await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
            .PatchAsync(new UpdateRealmSettingsDto { SignIn = signIn }, Ct);
        Assert.False(result.IsError, string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    private async Task<ErrorOr.ErrorOr<ApplicationSettingsDto>> PatchAppSignInAsync(Guid appId, ApplicationSignInDto signIn)
    {
        using var scope = TenantScope();
        return await scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>()
            .PatchAsync(appId, new ApplicationSettingsDto { SignIn = signIn }, Ct);
    }

    private async Task WithUserAsync(Func<UserManager<ApplicationUser>, ApplicationUser, Task> change)
    {
        using var scope = TenantScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(DefaultUser!.Id.ToString()) ?? throw new InvalidOperationException("no user");
        await change(users, user);
    }

    private Task EnableTotpAsync() => WithUserAsync(async (users, user) =>
    {
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
    });

    private Task RemovePasswordAsync() => WithUserAsync((users, user) => users.RemovePasswordAsync(user));

    private Task EnableEmailSecondFactorAsync() => WithUserAsync((users, user) =>
    {
        user.EmailOtpEnabled = true;
        return users.UpdateAsync(user);
    });

    private async Task<string> TotpCodeAsync()
    {
        var securityData = await Factory.GetDocumentAsync<UserSecurityData>(DefaultUser!.Id);
        return AuthEnforcementTests.GenerateTotpForTest(securityData!.AuthenticatorKey!);
    }

    private async Task ExpireSetupGraceAsync()
    {
        await using var session = GetTenantedDocumentSession();
        var security = await session.LoadAsync<UserSecurityData>(DefaultUser!.Id, Ct) ?? UserSecurityData.Create(DefaultUser!.Id);
        security.SecureSetupDueAt = DateTime.UtcNow.AddDays(-30);
        session.Store(security);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedPasskeyAsync(string? rpId)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Store(new StoredPasskeyCredential
        {
            Id = id,
            UserId = DefaultUser!.Id,
            CredentialId = RandomNumberGenerator.GetBytes(32),
            PublicKey = RandomNumberGenerator.GetBytes(64),
            UserHandle = DefaultUser!.Id.ToByteArray(),
            DisplayName = "Passkey",
            RpId = rpId,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<string> IssueCodeAsync()
    {
        Mail.Clear();
        using var scope = TenantScope();
        var result = await scope.ServiceProvider.GetRequiredService<Modgud.Authentication.Identity.IEmailOtpService>()
            .RequestNativeOtpAsync(DefaultUser!.Id, Ct);
        Assert.False(result.IsError, "RequestNativeOtpAsync failed to issue a challenge");
        var email = Mail.GetLastEmailTo(TestEmail);
        Assert.NotNull(email);
        return System.Text.RegularExpressions.Regex.Match(email!.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;
    }

    private Task<HttpClient> SessionAsync(params (string Method, TimeSpan Ago)[] factors) => SessionAsync(true, factors);

    /// <summary>A browser session as a sign-in with exactly these factors, proven that long
    /// ago, would have left it; <paramref name="recorded"/> false forges one from a release
    /// that did not record factors.</summary>
    private async Task<HttpClient> SessionAsync(bool recorded, params (string Method, TimeSpan Ago)[] factors)
    {
        using var scope = TenantScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(DefaultUser!.Id.ToString()) ?? throw new InvalidOperationException("no user");
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        SignInAssurance.Stamp(identity, factors.ToDictionary(f => f.Method, f => DateTimeOffset.UtcNow - f.Ago));
        if (recorded) SignInAssurance.MarkRecorded(identity);

        var created = await scope.ServiceProvider.GetRequiredService<ISessionService>()
            .CreateSessionAsync(user.Id, ipAddress: null, userAgent: nameof(SignInScenarioTests), Ct);
        Assert.False(created.IsError);
        identity.AddClaim(new Claim(SessionClaimTypes.BrowserSessionId, created.Value.Id.ToString()));

        var cookieOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(6),
        }, IdentityConstants.ApplicationScheme);
        string cookie;
        using (TenantContext.Enter(TenantConstants.SystemTenantId))
            cookie = cookieOptions.TicketDataFormat.Protect(ticket);

        var handler = new CookieContainerHandler();
        handler.Seed(new Uri("http://localhost"), cookieOptions.Cookie.Name!, cookie);
        return Factory.CreateDefaultClient(handler);
    }

    private static async Task<JsonElement> StepUpAsync(HttpClient browser, string returnUrl, bool reauthenticate = false)
    {
        var response = await browser.PostAsJsonAsync("/api/account/step-up",
            new { ReturnUrl = returnUrl, Reauthenticate = reauthenticate }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<string?> AuthorizeLocationAsync(HttpClient browser, string clientId)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var uri = "/connect/authorize?" + string.Join("&",
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            "scope=openid",
            $"state={Guid.NewGuid():N}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256");
        var response = await browser.GetAsync(uri, Ct);
        return response.Headers.Location?.ToString();
    }

    /// <summary>A public client bound to a new App with this sign-in override, written
    /// straight into the document (as a policy saved before the floor rule would be).</summary>
    private async Task<string> CreateAppClientAsync(
        SignInLevel minimumLevel, bool? totp = null, OwnFactorNotOffered? ownFactor = null)
    {
        var clientId = $"sc-{Guid.NewGuid():N}"[..16];
        var app = await CreateAppAsync(clientId);
        var overrides = new ApplicationSignInOverrides { MinimumLevel = minimumLevel, Totp = totp, OwnFactorNotOffered = ownFactor };
        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(new ApplicationSettings { Id = app.Id, CreatedAt = DateTimeOffset.UtcNow, SignIn = overrides });
            await session.SaveChangesAsync(Ct);
        }

        using var scope = TenantScope();
        var created = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientType = OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [RedirectUri],
            PostLogoutRedirectUris = [],
            Scopes = ["openid"],
            AllowedGrantTypes = ["authorization_code"],
            RequireConsent = false,
            AppIds = [new ShortGuid(app.Id).ToString()],
        }, Ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return clientId;
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(Ct);
        return (await session.LoadAsync<App>(id, Ct))!;
    }
}
