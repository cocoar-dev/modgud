using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.SignIn;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// ADR 0025 upgrade path. A browser cookie issued before sign-in factors were recorded
/// (0.14) carries none. Read naively it is a session that proved nothing, and the first
/// release with sign-in levels stopped every such admin at "2FA setup required" — on
/// production, for a user whose only usable second factor needed a password first. Such a
/// session keeps the standing 0.14 gave it until it ends; a cookie that recorded its factors
/// never gets that treatment.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SignInLevelUpgradeTests : IntegrationTestBase
{
    public SignInLevelUpgradeTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task A_session_from_before_factors_were_recorded_keeps_access_for_a_user_with_a_second_factor()
    {
        // The production case: the realm never saved its policy (administration derived to
        // multi), the grace ran out long ago, and the user's second factor cannot be offered
        // to this session — a passkey a native app enrolled under its own RP ID.
        await RequireMultiForAdministrationAsync();
        await ExpireSetupGraceAsync();
        await SeedPasskeyAsync(rpId: "app-dev.example-app.test");
        var legacy = await CreateForgedCookieClientAsync(recorded: false);

        var first = await legacy.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The renewed cookie carries the upgrade; the session stays where it was.
        var second = await legacy.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task A_session_from_before_factors_were_recorded_is_held_to_the_setup_without_a_second_factor()
    {
        // 0.14 stopped this user too: no second factor and the grace is over.
        await RequireMultiForAdministrationAsync();
        await ExpireSetupGraceAsync();
        var legacy = await CreateForgedCookieClientAsync(recorded: false);

        var response = await legacy.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(body.GetProperty("RequiresSecureSetup").GetBoolean());
    }

    [Fact]
    public async Task A_session_that_recorded_no_factors_is_not_mistaken_for_one_from_before()
    {
        // Same user, same second factor — but the cookie says its factors were recorded, so
        // "none" means none: the upgrade path must not be a way around the requirement.
        await RequireMultiForAdministrationAsync();
        await ExpireSetupGraceAsync();
        await SeedPasskeyAsync(rpId: "app-dev.example-app.test");
        var recorded = await CreateForgedCookieClientAsync(recorded: true);

        var response = await recorded.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_sign_in_records_its_factors_even_when_it_proved_none_worth_naming()
    {
        // Every cookie issued from now on carries the marker; that is what keeps the
        // upgrade path closed for it.
        var client = await CreateAuthenticatedClientAsync("tu", DefaultPassword);
        await RequireMultiForAdministrationAsync();
        await ExpireSetupGraceAsync();
        await SeedPasskeyAsync(rpId: "app-dev.example-app.test");

        var response = await client.GetAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ──

    private async Task RequireMultiForAdministrationAsync()
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { [TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId },
            };
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var result = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto { AdministrationMinimumLevel = "Multi" },
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    private async Task ExpireSetupGraceAsync()
    {
        await using var session = GetTenantedDocumentSession();
        var security = await session.LoadAsync<UserSecurityData>(DefaultUser!.Id, TestContext.Current.CancellationToken)
                       ?? UserSecurityData.Create(DefaultUser!.Id);
        security.SecureSetupDueAt = DateTime.UtcNow.AddDays(-30);
        session.Store(security);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedPasskeyAsync(string? rpId)
    {
        await using var session = GetTenantedDocumentSession();
        session.Store(new StoredPasskeyCredential
        {
            Id = Guid.NewGuid(),
            UserId = DefaultUser!.Id,
            CredentialId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            PublicKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64),
            UserHandle = DefaultUser!.Id.ToByteArray(),
            DisplayName = "Passkey",
            RpId = rpId,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A cookie as 0.14 issued it (no factor claims, no marker) — or, with
    /// <paramref name="recorded"/>, as 0.15 issues one for a sign-in that proved nothing.
    /// Forged with the app's own principal factory, session row and ticket format, like the
    /// federated cookie tests do.</summary>
    private async Task<HttpClient> CreateForgedCookieClientAsync(bool recorded)
    {
        using var scope = Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(DefaultUser!.Id.ToString())
                   ?? throw new InvalidOperationException("default user not found");
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        if (recorded) SignInAssurance.MarkRecorded(identity);

        using (TenantContext.Enter(TenantConstants.SystemTenantId))
        {
            var created = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .CreateSessionAsync(user.Id, ipAddress: null, userAgent: nameof(SignInLevelUpgradeTests),
                    TestContext.Current.CancellationToken);
            Assert.False(created.IsError, created.IsError ? created.FirstError.Description : null);
            identity.AddClaim(new Claim(SessionClaimTypes.BrowserSessionId, created.Value.Id.ToString()));
        }

        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(6),
        }, IdentityConstants.ApplicationScheme);

        string cookieValue;
        using (TenantContext.Enter(TenantConstants.SystemTenantId))
            cookieValue = cookieOptions.TicketDataFormat.Protect(ticket);

        var handler = new CookieContainerHandler();
        handler.Seed(new Uri("http://localhost"), cookieOptions.Cookie.Name!, cookieValue);
        return Factory.CreateDefaultClient(handler);
    }
}
