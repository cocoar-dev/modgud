using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// The mailbox is the binding way back (user decision 2026-11-08). A user without a password
/// signs in with an e-mail code or magic link; when their passkey device is gone, that fresh
/// sign-in also confirms removing the lost passkey and adding a new one. Before, only a fresh
/// passkey or TOTP — or a password plus an e-mail code — confirmed a factor change, so such a
/// user could sign in but never replace the passkey. A realm that switches "e-mail code after a
/// password" off does not accept the mailbox as a way back here either.
/// </summary>
public partial class EmailFallbackSecondFactorTests
{
    private const string PasswordlessEmail = "lost-device@test.com";

    [Fact]
    public async Task A_passwordless_user_whose_passkey_is_gone_removes_it_after_an_e_mail_code_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchSignInAsync(new UpdateSignInPolicyDto { EmailCode = true });
        var user = await PasswordlessUserAsync();
        var lostPasskey = await SeedPasskeyAsync(user.Id);
        var browser = await SignInWithEmailCodeAsync(user.Id);

        var removed = await browser.DeleteAsync($"/api/account/passkey/{lostPasskey}", ct);

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(ct));
        Assert.Null(await Factory.GetDocumentAsync<StoredPasskeyCredential>(lostPasskey));
    }

    [Fact]
    public async Task A_realm_without_the_e_mail_way_back_still_asks_a_passwordless_user_for_the_passkey()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchSignInAsync(new UpdateSignInPolicyDto { EmailCode = true, EmailAfterPassword = false });
        var user = await PasswordlessUserAsync();
        var passkey = await SeedPasskeyAsync(user.Id);
        var browser = await SignInWithEmailCodeAsync(user.Id);

        var refused = await browser.DeleteAsync($"/api/account/passkey/{passkey}", ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.GetProperty("RequiresReauthentication").GetBoolean());
        Assert.Equal(["passkey"], body.GetProperty("Methods").EnumerateArray().Select(m => m.GetString()));
    }

    private async Task<Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView> PasswordlessUserAsync()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Lost", lastname: "Device", acronym: "LD", email: PasswordlessEmail, password: DefaultPassword);
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { [Modgud.Infrastructure.Persistence.Tenancy.TenantConstants.HttpContextTenantIdKey] =
                    Modgud.Infrastructure.Persistence.Tenancy.TenantConstants.SystemTenantId },
            };
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var identity = await users.FindByIdAsync(user.Id.ToString());
        var removed = await users.RemovePasswordAsync(identity!);
        Assert.True(removed.Succeeded, string.Join(", ", removed.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<HttpClient> SignInWithEmailCodeAsync(Guid userId)
    {
        var ct = TestContext.Current.CancellationToken;
        var code = await IssueSignInCodeAsync(userId, PasswordlessEmail);
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var login = await browser.PostAsJsonAsync("/api/account/passwordless-otp/login",
            new { Email = PasswordlessEmail, Code = code }, ct);
        var body = await login.Content.ReadAsStringAsync(ct);
        Assert.True(login.StatusCode == HttpStatusCode.OK && body.Contains("Login successful"), body);
        return browser;
    }
}
