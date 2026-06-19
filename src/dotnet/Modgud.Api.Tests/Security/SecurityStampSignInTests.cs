using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Regression tests for the SecurityStamp-sign-in bug class (AppBase v5.1.1/v5.1.2).
///
/// The security stamp lives authoritatively on the <see cref="UserSecurityData"/>
/// document; <c>ApplicationUser.SecurityStamp</c> is only a transient mirror that
/// is hydrated by the <c>UserManager</c> finders (FindById/FindByName →
/// PopulateSecurityDataAsync). Two defects make non-password sign-in mint a
/// cookie that fails the next <c>SecurityStampValidator</c> pass:
///   1. <see cref="EventSourcedUserStore.GetSecurityStampAsync"/> returns the
///      transient mirror instead of re-fetching the authoritative value (unlike
///      GetPasswordHashAsync, which does re-fetch).
///   2. <see cref="EventSourcedUserStore.CreateAsync"/> seeds the UserSecurityData
///      stamp with an independent GUID, so mirror and authoritative diverge from
///      birth.
/// Magic-link and passkey-web raw-load the user (so the cookie carries the stale
/// mirror); OIDC/SAML hand-build the principal with no stamp claim at all.
///
/// These tests are RED until the fix lands. They are split into:
///   • store-level root-cause tests (deterministic, pin defects 1 + 2) — these
///     cover the magic-link AND passkey-web paths, which share the same raw-load
///     + SignInAsync mechanism;
///   • an end-to-end magic-link test that reproduces the user-visible symptom
///     (silent logout within the validation interval).
/// The OIDC/SAML root cause (missing stamp claim on the hand-built principal) is
/// pinned in <c>ExternalLoginProcessorTests</c>.
///
/// The end-to-end test relies on the harness forcing
/// <c>SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero</c>
/// (see <c>ModgudWebApplicationFactory.ConfigureWebHost</c>); without it the
/// 5-minute production cache window masks the bug.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityStampSignInTests : IntegrationTestBase
{
    public SecurityStampSignInTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetSecurityStampAsync_ReturnsAuthoritativeStamp_NotStaleMirror()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Stamp", lastname: "Authoritative", acronym: "SA",
            email: "stamp-authoritative@test.com", password: "TestPass1234");

        await using var session = GetTenantedDocumentSession();
        var authoritative = (await session.LoadAsync<UserSecurityData>(user.Id, ct))!.SecurityStamp;

        // Reproduce the exact condition that holds for every raw-loaded user: the
        // in-memory ApplicationUser mirror diverges from the authoritative stamp.
        var appUser = (await session.LoadAsync<ApplicationUser>(user.Id, ct))!;
        appUser.SecurityStamp = "stale-mirror-value-not-the-authoritative-stamp";

        var store = new EventSourcedUserStore(session);
        var actual = await store.GetSecurityStampAsync(appUser, ct);

        // The store must re-fetch the authoritative value (like GetPasswordHashAsync),
        // not echo back the stale in-memory mirror. RED today.
        Assert.Equal(authoritative, actual);
    }

    [Fact]
    public async Task CreateAsync_AlignsApplicationUserStampWithSecurityData()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Stamp", lastname: "Aligned", acronym: "SL",
            email: "stamp-aligned@test.com", password: "TestPass1234");

        await using var session = GetTenantedDocumentSession();
        var appStamp = (await session.LoadAsync<ApplicationUser>(user.Id, ct))!.SecurityStamp;
        var securityStamp = (await session.LoadAsync<UserSecurityData>(user.Id, ct))!.SecurityStamp;

        // After creation the mirror and the authoritative stamp must match — they
        // are seeded as two independent GUIDs today, so this is RED.
        Assert.Equal(securityStamp, appStamp);
    }

    [Fact]
    public async Task MagicLinkLogin_CookieSurvivesStampRevalidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var client = Factory.CreateDefaultClient(new CookieContainerHandler());

        await client.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "test@test.com" }, ct);

        var email = emailService.GetLastEmailTo("test@test.com");
        Assert.NotNull(email);
        var (userId, token) = ExtractMagicLinkParams(email!.HtmlBody);
        Assert.NotNull(userId);
        Assert.NotNull(token);

        // The login itself succeeds — the auth cookie is minted.
        var login = await client.PostAsJsonAsync("/api/account/magic-link/login",
            new { UserId = userId, Token = token }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The next authenticated request triggers the SecurityStampValidator
        // (ValidationInterval = Zero in the harness). Today the magic-link cookie
        // carries the stale ApplicationUser mirror stamp, which != the authoritative
        // UserSecurityData stamp → the cookie is rejected → 401. RED until the fix.
        var me = await client.GetAsync("/api/account/me", ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    private static (string? UserId, string? Token) ExtractMagicLinkParams(string htmlBody)
    {
        var hrefMatch = Regex.Match(htmlBody, @"href=""([^""]*magic-login[^""]*)""");
        if (!hrefMatch.Success) return (null, null);

        var uri = new Uri(hrefMatch.Groups[1].Value);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["userId"], query["token"]);
    }
}
