using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Regression tests for the SecurityStamp-sign-in bug class (AppBase v5.1.1/v5.1.2).
///
/// The security stamp lives authoritatively on the <see cref="UserSecurityData"/>
/// document; <c>ApplicationUser.SecurityStamp</c> is only a transient mirror that
/// is hydrated by the <c>UserManager</c> finders (FindById/FindByName →
/// PopulateSecurityDataAsync). Two defects made non-password sign-in mint a
/// cookie that the <c>SecurityStampValidator</c> rejected on its next pass
/// (silent logout):
///   1. <see cref="EventSourcedUserStore.GetSecurityStampAsync"/> returned the
///      transient mirror instead of re-fetching the authoritative value (unlike
///      GetPasswordHashAsync, which does re-fetch).
///   2. <see cref="EventSourcedUserStore.CreateAsync"/> seeded the UserSecurityData
///      stamp with an independent GUID, so mirror and authoritative diverged from
///      birth.
/// Magic-link and passkey-web raw-load the user (so the cookie carried the stale
/// mirror); OIDC/SAML hand-build the principal.
///
/// These two store-level tests pin the root cause deterministically and cover the
/// magic-link AND passkey-web paths, which share the same raw-load + SignInAsync
/// mechanism. The end-to-end symptoms are guarded elsewhere, all enabled by the
/// harness forcing <c>SecurityStampValidatorOptions.ValidationInterval = Zero</c>
/// (see <c>ModgudWebApplicationFactory</c>):
///   • magic-link: <c>MagicLinkTests.MagicLink_FullFlow_CompletesSignIn</c>
///     (login + a second authenticated request — a stamp revalidation);
///   • OIDC/SAML principal stamp claim:
///     <c>ExternalLoginProcessorTests.ProcessAsync_ReturnedPrincipal_CarriesAuthoritativeSecurityStampClaim</c>;
///   • federated session-group survival across revalidation:
///     <c>UserInfoPerAudienceTests</c> *_Federated_* (the Modgud-specific amplifier).
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
        // not echo back the stale in-memory mirror.
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
        // were seeded as two independent GUIDs before the fix.
        Assert.Equal(securityStamp, appStamp);
    }
}
