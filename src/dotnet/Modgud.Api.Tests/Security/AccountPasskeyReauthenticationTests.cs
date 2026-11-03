using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Api.Cookies;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.SignIn;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B8-AS02: stale account proof -> guard -> reauthentication -> real signed
/// WebAuthn assertion -> guarded mutation. Synthetic mapped hosts, no native
/// grants or browser authenticator. Never print proofs, keys or cookie values.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AccountPasskeyReauthenticationTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string AppRp = "app.example.test";
    private const string ProfileHost = "portal.app.example.test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Match production cookie scope when arranging the initial stale session;
    // otherwise a seeded host-only cookie shadows the renewed realm-domain one.
    private sealed class AccountCookies(string primaryDomain) : DelegatingHandler
    {
        private readonly CookieContainer _cookies = new();

        public void Seed(Uri origin, string name, string value)
        {
            var cookie = new Cookie(name, value) { Path = "/" };
            if (TenantApexCookieManager.ResolveCookieDomain(origin.Host, primaryDomain) is { } domain)
                cookie.Domain = domain;
            _cookies.Add(origin, cookie);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = _cookies.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(header)) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) _cookies.SetCookies(request.RequestUri!, value);
            return response;
        }
    }

    private sealed record Browser(HttpClient Client, AccountCookies Cookies, Uri Origin) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    [Theory]
    [InlineData(AppRp)]
    [InlineData(ProfileHost)]
    public async Task App_passkey_recognized_by_the_guard_can_confirm_the_account_on_its_host(string host)
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var credentialId = await CredentialAsync(authenticator, DefaultUser.Id, AppRp);
        using var browser = await StaleBrowserAsync(host);
        await RequireAccountProofAsync(browser, credentialId);

        var options = await OptionsAsync(browser);
        Assert.Equal(AppRp, options.GetProperty("rpId").GetString());
        Assert.Equal("required", options.GetProperty("userVerification").GetString());
        Assert.True(ContainsOnly(options, authenticator));
        var assertion = authenticator.CreateAssertionJson(
            options.GetProperty("challenge").GetString()!, AppRp, browser.Origin.GetLeftPart(UriPartial.Authority));
        using var signedIn = await AssertAsync(browser, assertion);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        await using var session = GetTenantedDocumentSession();
        var ceremony = await session.Query<PasskeyCeremony>().SingleAsync(Ct);
        Assert.True(ceremony.IsConsumed);
        Assert.True(ceremony.AccountReauthenticationUserId == DefaultUser.Id);
        using var changed = await browser.Client.DeleteAsync($"/api/account/passkey/{credentialId}", Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Null(await Factory.GetDocumentAsync<StoredPasskeyCredential>(credentialId));

        // Consumed ceremony cannot be replayed, including after the account change.
        browser.Cookies.Seed(browser.Origin, "Modgud.Passkey.Challenge", ceremony.Id.ToString());
        using var replay = await AssertAsync(browser, assertion);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_realm_passkey_keeps_its_rp_and_can_confirm_from_a_realm_subdomain(bool editPrimary)
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var credentialId = await CredentialAsync(authenticator, DefaultUser.Id, rpId: null);
        // Isolate RP pinning from changing cookie Domain from a parent to host-only.
        // The latter subdomain/configuration-switch case is tracked separately.
        var host = editPrimary ? "auth.example.test" : "portal.auth.example.test";
        using var browser = await StaleBrowserAsync(host, primaryDomain: "auth.example.test");
        await RequireAccountProofAsync(browser, credentialId);
        var options = await OptionsAsync(browser);
        Assert.Equal("auth.example.test", options.GetProperty("rpId").GetString());
        Assert.True(ContainsOnly(options, authenticator));
        if (editPrimary)
        {
            await using var global = Factory.Services.GetRequiredService<IGlobalStore>().LightweightSession();
            var realm = await global.Query<Realm>().SingleAsync(r => r.Slug == "system", Ct);
            realm.PrimaryDomain = "changed.example.test";
            global.Store(realm);
            await global.SaveChangesAsync(Ct);
            Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
        }
        using var login = await AssertAsync(browser, authenticator.CreateAssertionJson(
            options.GetProperty("challenge").GetString()!, "auth.example.test", "https://" + host));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var changed = await browser.Client.DeleteAsync($"/api/account/passkey/{credentialId}", Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    [Fact]
    public async Task Ordinary_login_does_not_select_an_account_rp_from_username_or_caller_flags()
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await CredentialAsync(authenticator, DefaultUser.Id, AppRp);
        using var browser = await StaleBrowserAsync(ProfileHost);
        // No authenticated server-issued account-reauthentication partial.
        using var response = await browser.Client.PostAsJsonAsync("/api/account/passkey/login-options",
            new { ReturnUrl = "/profile", UserName = "tu", Reauthenticate = true, RpId = AppRp }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("localhost", options.GetProperty("rpId").GetString());
    }

    [Fact]
    public async Task A_foreign_rp_is_not_an_account_factor_or_a_reauthentication_target()
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await CredentialAsync(authenticator, DefaultUser.Id, "foreign.example.test");
        using var browser = await StaleBrowserAsync(ProfileHost);
        using var step = await browser.Client.PostAsJsonAsync("/api/account/step-up",
            new { ReturnUrl = "/profile", Reauthenticate = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        var body = await step.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.GetProperty("RequiresFirstFactor").GetBoolean());
        Assert.False(body.GetProperty("Methods").EnumerateArray().Any(m => m.GetString() == "passkey"));
        var options = await OptionsAsync(browser);
        Assert.Equal("localhost", options.GetProperty("rpId").GetString());
    }

    [Fact]
    public async Task Reauthentication_ignores_a_foreign_username_and_rejects_another_users_valid_assertion()
    {
        using var own = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var ownId = await CredentialAsync(own, DefaultUser.Id, AppRp);
        var other = await Factory.CreateTestUserWithIdentityAsync("Other", "Owner", "oo", "other@example.test");
        using var foreign = new SoftwareWebAuthnAuthenticator(other.Id.ToByteArray());
        await CredentialAsync(foreign, other.Id, AppRp);
        using var browser = await StaleBrowserAsync(ProfileHost);
        await RequireAccountProofAsync(browser, ownId);

        var options = await OptionsAsync(browser, "oo");
        Assert.True(ContainsOnly(options, own));
        using var refused = await AssertAsync(browser, foreign.CreateAssertionJson(
            options.GetProperty("challenge").GetString()!, AppRp, "https://" + ProfileHost));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var mutation = await browser.Client.DeleteAsync($"/api/account/passkey/{ownId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
    }

    [Theory]
    [InlineData("foreign-origin")]
    [InlineData("cross-port")]
    [InlineData("canonical-but-not-request-origin")]
    [InlineData("wrong-rp-hash")]
    [InlineData("no-uv")]
    [InlineData("wrong-challenge")]
    public async Task Reauthentication_retains_origin_rp_uv_and_challenge_verification(string failure)
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var id = await CredentialAsync(authenticator, DefaultUser.Id, AppRp);
        using var browser = await StaleBrowserAsync(ProfileHost);
        await RequireAccountProofAsync(browser, id);
        var options = await OptionsAsync(browser);
        Assert.Equal(AppRp, options.GetProperty("rpId").GetString());
        var challenge = options.GetProperty("challenge").GetString()!;
        if (failure == "wrong-challenge")
        {
            // A new server ceremony replaces the cookie; the previous challenge is stale.
            await OptionsAsync(browser);
        }
        var origin = failure switch
        {
            "foreign-origin" => "https://foreign.example.test",
            "cross-port" => "https://" + ProfileHost + ":444",
            "canonical-but-not-request-origin" => "https://" + AppRp,
            _ => "https://" + ProfileHost,
        };
        var assertion = authenticator.CreateAssertionJson(challenge,
            failure == "wrong-rp-hash" ? "foreign.example.test" : AppRp,
            origin, userVerified: failure != "no-uv");
        using var refused = await AssertAsync(browser, assertion);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var mutation = await browser.Client.DeleteAsync($"/api/account/passkey/{id}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
    }

    [Fact]
    public async Task A_partial_for_another_account_cannot_select_its_credentials()
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var id = await CredentialAsync(authenticator, DefaultUser.Id, AppRp);
        using var browser = await StaleBrowserAsync(ProfileHost);
        await RequireAccountProofAsync(browser, id);
        var other = await Factory.CreateTestUserWithIdentityAsync("Other", "Session", "os", "session@example.test");
        await SeedAuthenticationAsync(browser.Cookies, browser.Origin, other.Id);
        using var options = await browser.Client.PostAsJsonAsync("/api/account/passkey/login-options",
            new { ReturnUrl = "/profile" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, options.StatusCode);
    }

    [Fact]
    public async Task Removed_account_credential_is_not_replaced_by_another_users_or_foreign_rp()
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var id = await CredentialAsync(authenticator, DefaultUser.Id, AppRp);
        using var foreignRp = new SoftwareWebAuthnAuthenticator(DefaultUser.Id.ToByteArray());
        await CredentialAsync(foreignRp, DefaultUser.Id, "foreign.example.test");
        var other = await Factory.CreateTestUserWithIdentityAsync("Other", "Remaining", "or", "remaining@example.test");
        using var foreignOwner = new SoftwareWebAuthnAuthenticator(other.Id.ToByteArray());
        await CredentialAsync(foreignOwner, other.Id, AppRp);
        using var browser = await StaleBrowserAsync(ProfileHost);
        await RequireAccountProofAsync(browser, id);
        await using (var session = GetTenantedDocumentSession())
        {
            session.Delete<StoredPasskeyCredential>(id);
            await session.SaveChangesAsync(Ct);
        }
        using var response = await browser.Client.PostAsJsonAsync("/api/account/passkey/login-options",
            new { ReturnUrl = "/profile" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("owner-changed")]
    [InlineData("partial-missing")]
    [InlineData("ceremony-expired")]
    public async Task The_live_account_binding_is_required_when_redeeming(string failure)
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var id = await CredentialAsync(authenticator, DefaultUser.Id, AppRp);
        using var browser = await StaleBrowserAsync(ProfileHost);
        await RequireAccountProofAsync(browser, id);
        var options = await OptionsAsync(browser);
        Assert.Equal(AppRp, options.GetProperty("rpId").GetString());
        if (failure == "owner-changed")
        {
            var other = await Factory.CreateTestUserWithIdentityAsync("Other", "Redeemer", "rd", "redeemer@example.test");
            await SeedAuthenticationAsync(browser.Cookies, browser.Origin, other.Id);
        }
        else if (failure == "partial-missing")
        {
            var cookie = Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.TwoFactorUserIdScheme).Cookie.Name!;
            browser.Cookies.Seed(browser.Origin, cookie, "");
        }
        else
        {
            await using var session = GetTenantedDocumentSession();
            var ceremony = await session.Query<PasskeyCeremony>().SingleAsync(Ct);
            ceremony.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            session.Store(ceremony);
            await session.SaveChangesAsync(Ct);
        }
        using var response = await AssertAsync(browser, authenticator.CreateAssertionJson(
            options.GetProperty("challenge").GetString()!, AppRp, "https://" + ProfileHost));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var credential = await Factory.GetDocumentAsync<StoredPasskeyCredential>(id);
        Assert.NotNull(credential);
        Assert.Equal(0u, credential.SignatureCount);
    }

    [Theory]
    [InlineData("portal.app.auth.example.test", "app.auth.example.test", true, "auth.example.test")]
    [InlineData(ProfileHost, "example.test", false, AppRp)]
    public async Task Selection_prefers_the_eligible_realm_then_the_most_specific_owned_rp(
        string host, string alternativeRp, bool legacy, string expectedRp)
    {
        using var first = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        using var alternative = new SoftwareWebAuthnAuthenticator(DefaultUser.Id.ToByteArray());
        var id = await CredentialAsync(first, DefaultUser.Id, legacy ? null : AppRp);
        await CredentialAsync(alternative, DefaultUser.Id, alternativeRp);
        using var browser = await StaleBrowserAsync(host, primaryDomain: legacy ? "auth.example.test" : null);
        await RequireAccountProofAsync(browser, id);
        var options = await OptionsAsync(browser);
        Assert.Equal(expectedRp, options.GetProperty("rpId").GetString());
        Assert.True(ContainsOnly(options, first));
    }

    private async Task<Guid> CredentialAsync(SoftwareWebAuthnAuthenticator authenticator, Guid userId, string? rpId)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Store(new StoredPasskeyCredential
        {
            Id = id, UserId = userId, RpId = rpId,
            CredentialId = authenticator.CredentialId, PublicKey = authenticator.CosePublicKey(),
            UserHandle = authenticator.UserHandle, DisplayName = "Synthetic account proof",
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<Browser> StaleBrowserAsync(string host, string? primaryDomain = null)
    {
        // Declare the synthetic host in THIS work-owned realm; no DNS/network or
        // new tenant/container lifecycle is involved.
        var global = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = global.LightweightSession())
        {
            var realm = await session.Query<Realm>().SingleAsync(r => r.Slug == "system", Ct);
            if (!realm.Domains.Contains(host)) realm.Domains = [.. realm.Domains, host];
            // Global realm metadata survives the tenant-data reset between tests.
            realm.PrimaryDomain = primaryDomain ?? "localhost";
            session.Store(realm);
            await session.SaveChangesAsync(Ct);
        }
        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
        var origin = new Uri("https://" + host);
        var cookies = new AccountCookies(primaryDomain ?? "localhost");
        await SeedAuthenticationAsync(cookies, origin, DefaultUser!.Id);
        var client = Factory.CreateDefaultClient(cookies);
        client.BaseAddress = origin;
        return new Browser(client, cookies, origin);
    }

    private async Task SeedAuthenticationAsync(AccountCookies cookies, Uri origin, Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        var principal = await signIn.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        // A real persisted session and production cookie format with exactly the
        // stale factor times under test; no clock changes or expired auth ticket.
        SignInAssurance.Stamp(identity, new Dictionary<string, DateTimeOffset>
        {
            [SignInMethods.Email] = DateTimeOffset.UtcNow.AddMinutes(-30),
        });
        SignInAssurance.MarkRecorded(identity);
        var created = await scope.ServiceProvider.GetRequiredService<ISessionService>()
            .CreateSessionAsync(user.Id, null, nameof(AccountPasskeyReauthenticationTests), Ct);
        Assert.False(created.IsError);
        identity.AddClaim(new Claim(SessionClaimTypes.BrowserSessionId, created.Value.Id.ToString()));
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        }, IdentityConstants.ApplicationScheme);
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        cookies.Seed(origin, options.Cookie.Name!, options.TicketDataFormat.Protect(ticket));
    }

    private static async Task RequireAccountProofAsync(Browser browser, Guid credentialId)
    {
        using var refusal = await browser.Client.DeleteAsync($"/api/account/passkey/{credentialId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
        var body = await refusal.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.GetProperty("RequiresReauthentication").GetBoolean());
        Assert.Equal(["passkey"], body.GetProperty("Methods").EnumerateArray().Select(m => m.GetString()));
        using var step = await browser.Client.PostAsJsonAsync("/api/account/step-up",
            new { ReturnUrl = "/profile", Reauthenticate = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        var continuation = await step.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(continuation.GetProperty("RequiresMfa").GetBoolean());
        Assert.Equal(["passkey"], continuation.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));
    }

    private static async Task<JsonElement> OptionsAsync(Browser browser, string? username = null)
    {
        using var response = await browser.Client.PostAsJsonAsync("/api/account/passkey/login-options",
            new { ReturnUrl = "/profile", UserName = username }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static bool ContainsOnly(JsonElement options, SoftwareWebAuthnAuthenticator authenticator)
    {
        var credentials = options.GetProperty("allowCredentials").EnumerateArray().ToArray();
        return credentials.Length == 1 &&
            credentials[0].GetProperty("id").GetString() == SoftwareWebAuthnAuthenticator.B64Url(authenticator.CredentialId);
    }

    private static Task<HttpResponseMessage> AssertAsync(Browser browser, string assertion) =>
        browser.Client.PostAsync("/api/account/passkey/login",
            new StringContent(assertion, Encoding.UTF8, "application/json"), Ct);
}
