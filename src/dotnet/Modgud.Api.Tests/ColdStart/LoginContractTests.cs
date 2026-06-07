using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 3 (login): password-login API contracts — the happy path plus every
/// failure branch. All failures must return the same uniform 401 (anti-
/// enumeration), missing fields a 400, and a 2FA-enabled user the RequiresMfa
/// signal. Includes a regression guard that a soft-deleted user cannot sign in
/// (the store filters IsDeleted at the query layer).
/// </summary>
public class LoginContractTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    private async Task<UserView> ArrangeUserAsync(string acronym, string email)
        => await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Login", lastname: "User", acronym: acronym, email: email,
            password: "TestPass1234", isRealmAdmin: false);

    private async Task PatchUserAsync(Guid id, Action<ApplicationUser> patch, CancellationToken ct)
    {
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        await using var s = store.LightweightSession("system");
        var u = await s.LoadAsync<ApplicationUser>(id, ct);
        patch(u!);
        s.Store(u);
        await s.SaveChangesAsync(ct);
    }

    private Task<HttpResponseMessage> LoginAsync(string userName, string password, CancellationToken ct)
        => Factory.CreateClient().PostAsJsonAsync("/api/account/login",
            new { UserName = userName, Password = password }, ct);

    [Fact]
    public async Task Login_with_correct_credentials_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await ArrangeUserAsync("loginok", "loginok@cli.local");

        var resp = await LoginAsync("loginok", "TestPass1234", ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Login successful", await resp.Content.ReadAsStringAsync(ct));
        Assert.Contains(resp.Headers, h => h.Key == "Set-Cookie");
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_uniform_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await ArrangeUserAsync("loginwp", "loginwp@cli.local");

        var resp = await LoginAsync("loginwp", "WrongPass9999", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Invalid credentials", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Login_with_unknown_user_returns_the_same_uniform_401()
    {
        var ct = TestContext.Current.CancellationToken;

        var resp = await LoginAsync("nobody-here", "TestPass1234", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Invalid credentials", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Login_with_inactive_user_returns_401_even_with_correct_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await ArrangeUserAsync("loginina", "loginina@cli.local");
        await PatchUserAsync(user.Id, u => u.IsActive = false, ct);

        var resp = await LoginAsync("loginina", "TestPass1234", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_with_soft_deleted_user_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await ArrangeUserAsync("logindel", "logindel@cli.local");
        // Soft-delete: the store's FindByName/FindByEmail filter !IsDeleted, so a
        // deleted user can never authenticate (defense the passkey path mirrors).
        await PatchUserAsync(user.Id, u => u.IsDeleted = true, ct);

        var resp = await LoginAsync("logindel", "TestPass1234", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_with_missing_fields_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;

        var resp = await Factory.CreateClient().PostAsJsonAsync("/api/account/login",
            new { UserName = "", Password = "" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_for_a_2fa_user_returns_the_requires_mfa_signal()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await ArrangeUserAsync("login2fa", "login2fa@cli.local");
        await PatchUserAsync(user.Id, u => u.EmailOtpEnabled = true, ct);

        var resp = await LoginAsync("login2fa", "TestPass1234", ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("RequiresMfa", body);
        Assert.Contains("email", body);
    }
}
