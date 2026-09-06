using System.Security.Claims;
using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authorization.Principals;

using Wolverine;

namespace Modgud.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class ExternalLoginProcessorTests : IntegrationTestBase
{
    public ExternalLoginProcessorTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Issuer = "https://login.microsoftonline.com/test-tenant/v2.0";

    [Fact]
    public async Task Returning_User_SignsInAndUpdatesClaimsSnapshot()
    {
        var config = await CreateEnabledEntraConfig();
        var user = await Factory.CreateTestUserWithIdentityAsync("Rick", "Returns", "RR", "rick@acme.com");
        var linkId = await LinkUserAsync(user.Id, config.Id, subject: "sub-rick-1");

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(subject: "sub-rick-1", email: "rick@acme.com", name: "Rick Returns", groups: ["IT"]);
        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(linkId, result.LinkId);

        // Principal carries only the minimum session metadata for logout routing —
        // group/role claims are deliberately NOT stamped anymore (membership is
        // persistent, see Phase 10 refactor).
        var principal = result.Principal!;
        Assert.Equal(user.Id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(Issuer, principal.FindFirst("modgud.external.issuer")?.Value);
        Assert.Empty(principal.FindAll("modgud.external.group"));

        // Script-run snapshot was persisted on the link (debugging artifact).
        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var link = await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken);
        Assert.NotNull(link!.LastScriptOutput);
        Assert.True(link.LastScriptSucceeded);
    }

    /// <summary>
    /// Regression for the SecurityStamp-sign-in bug, OIDC/SAML leg. Both
    /// federated flows sign in the hand-built principal this
    /// processor returns (ExternalAuthEndpoints + SamlLoginFlow call
    /// <c>SignInAsync(ApplicationScheme, result.Principal)</c>). The principal must
    /// carry the user's authoritative security-stamp claim, otherwise the cookie
    /// has no stamp and the SecurityStampValidator rejects it on the first pass
    /// (≤ ValidationInterval) → every federated session is silently logged out.
    /// RED until the fix stamps the principal. See <c>SecurityStampSignInTests</c>
    /// for the magic-link/passkey legs and the store-level root cause.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ReturnedPrincipal_CarriesAuthoritativeSecurityStampClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateEnabledEntraConfig();
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Stan", "Stamp", "SS", "stan-stamp@acme.com");
        await LinkUserAsync(user.Id, config.Id, subject: "sub-stan-1");

        // Authoritative stamp lives on UserSecurityData (the cookie must carry it).
        string authoritativeStamp;
        using (var read = GetTenantedSession())
            authoritativeStamp = (await read.LoadAsync<UserSecurityData>(user.Id, ct))!.SecurityStamp;

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(subject: "sub-stan-1", email: "stan-stamp@acme.com", name: "Stan Stamp");
        var result = await processor.ProcessAsync(external, config.Id, ct);
        Assert.True(result.Succeeded);

        var stampClaim = result.Principal!.FindFirst("AspNet.Identity.SecurityStamp")?.Value;
        Assert.Equal(authoritativeStamp, stampClaim);
    }

    [Fact]
    public async Task JitCreation_CreatesUserAndLink_WhenAutoCreateOn()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: true);

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(
            subject: "sub-new-jit",
            email: "newbie@acme.com",
            name: "Newt Bienvenue");

        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.UserId);

        using var scope2 = Factory.Services.CreateScope();
        var userManager = scope2.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(result.UserId!.Value.ToString());
        Assert.NotNull(user);
        Assert.Equal("newbie@acme.com", user!.Email);
        Assert.Equal("Newt", user.Firstname);
        Assert.Equal("Bienvenue", user.Lastname);

        var session = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var principal = await session.LoadAsync<Modgud.Authorization.Principals.Person>(user.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(principal);
        Assert.Single(principal!.ExternalIdentities);
    }

    [Fact]
    public async Task NoLinkAndAutoCreateOff_Fails()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: false);

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(
            subject: "sub-stranger",
            email: "stranger@acme.com",
            name: "Sir Stranger");

        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.NoUserAndAutoCreateOff", result.ErrorCode);
    }

    [Fact]
    public async Task TrustForEmailLink_LinksToExistingUserByEmail()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: false, trustForEmailLink: true);
        var existing = await Factory.CreateTestUserWithIdentityAsync("Elin", "Existing", "EE", "elin@acme.com");

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(
            subject: "sub-elin-fresh",
            email: "elin@acme.com",
            name: "Elin Existing");

        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.True(result.Succeeded);
        Assert.Equal(existing.Id, result.UserId);

        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var link = await session.Query<ExternalIdentityLink>()
            .Where(l => l.Subject == "sub-elin-fresh")
            .FirstOrDefaultAsync();
        Assert.NotNull(link);
        Assert.Equal(existing.Id, link!.UserId);
    }

    [Fact]
    public async Task TrustForEmailLink_WithUnverifiedEmail_IsRejected()
    {
        // Audit H3: account-takeover guard. TrustForEmailLink must NOT absorb an
        // existing account when the IdP did not assert email_verified — otherwise
        // an attacker who self-registered the victim's email at a permissive OIDC
        // provider could sign in as the victim. Must fail closed (no link created).
        var config = await CreateEnabledEntraConfig(autoCreate: false, trustForEmailLink: true);
        var existing = await Factory.CreateTestUserWithIdentityAsync("Vic", "Tim", "VT", "victim@acme.com");

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(
            subject: "sub-attacker-unverified",
            email: "victim@acme.com",
            name: "Victim Tim",
            emailVerified: false);

        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.EmailNotVerified", result.ErrorCode);

        // No link was forged onto the victim's account.
        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var link = await session.Query<ExternalIdentityLink>()
            .Where(l => l.Subject == "sub-attacker-unverified")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        Assert.Null(link);
    }

    [Fact]
    public async Task AllowedDomains_RejectsMismatchedEmail()
    {
        var config = await CreateEnabledEntraConfig(
            autoCreate: true,
            allowedDomains: ["acme.com"]);

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal(
            subject: "sub-outsider",
            email: "outsider@contoso.com",
            name: "Out Sider");

        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.EmailNotAllowed", result.ErrorCode);
    }

    [Fact]
    public async Task UnlinkedLink_IsForgotten_AndRematchesByEmailPolicy()
    {
        // Variant C — "unlink forgets the binding". A disconnected IdP (here a
        // legacy IsUnlinked tombstone) is no longer hard-blocked with Idp.Unlinked;
        // the tombstone is forgotten and the login re-matches by policy. With
        // TrustForEmailLink on, the same email re-binds to the same account via a
        // fresh link.
        var config = await CreateEnabledEntraConfig(trustForEmailLink: true);
        var user = await Factory.CreateTestUserWithIdentityAsync("Re", "Link", "RL", "relink@acme.com");
        var linkId = await LinkUserAsync(user.Id, config.Id, subject: "sub-relink-1");

        // Legacy tombstone: the old soft-unlink path set IsUnlinked=true.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(linkId, new ExternalIdentityUnlinkedEvent(linkId, DateTimeOffset.UtcNow, user.Id));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var external = BuildExternalPrincipal("sub-relink-1", "relink@acme.com", "Re Link");
            var result = await processor.ProcessAsync(external, config.Id, default);
            Assert.True(result.Succeeded);
            Assert.Equal(user.Id, result.UserId);
        }

        // The tombstone is gone (hard-deleted) and a fresh live link owns the slot.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            Assert.Null(await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken));
            var live = await session.Query<ExternalIdentityLink>()
                .Where(l => l.Subject == "sub-relink-1" && !l.IsUnlinked)
                .FirstOrDefaultAsync();
            Assert.NotNull(live);
            Assert.Equal(user.Id, live!.UserId);
            Assert.NotEqual(linkId, live.Id);
        }
    }

    [Fact]
    public async Task UnlinkedLink_NoRematchPolicy_FallsThrough_NotIdpUnlinked()
    {
        // Without a re-match policy (AutoCreate off, no TrustForEmailLink), a
        // forgotten/unlinked identity falls through to the normal no-link gate —
        // proving it is genuinely re-evaluated by policy rather than hard-blocked
        // with the old Idp.Unlinked "disconnected" error. The slot is freed either way.
        var config = await CreateEnabledEntraConfig(autoCreate: false);
        var user = await Factory.CreateTestUserWithIdentityAsync("No", "Match", "NM", "nomatch@acme.com");
        var linkId = await LinkUserAsync(user.Id, config.Id, subject: "sub-nomatch-1");

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(linkId, new ExternalIdentityUnlinkedEvent(linkId, DateTimeOffset.UtcNow, user.Id));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var external = BuildExternalPrincipal("sub-nomatch-1", "nomatch@acme.com", "No Match");
            var result = await processor.ProcessAsync(external, config.Id, default);
            Assert.False(result.Succeeded);
            Assert.NotEqual("Idp.Unlinked", result.ErrorCode);
            Assert.Equal("Idp.NoUserAndAutoCreateOff", result.ErrorCode);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            Assert.Null(await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task LiveLink_DifferentAuthenticatedUser_IsRejected()
    {
        // The cross-user hijack guard: a LIVE link can never be stolen by a
        // different authenticated user (it is only re-homable once released).
        var config = await CreateEnabledEntraConfig();
        var userA = await Factory.CreateTestUserWithIdentityAsync("Owner", "Live", "OL", "ownerlive@acme.com");
        var userB = await Factory.CreateTestUserWithIdentityAsync("Other", "User", "OU", "otheruser@acme.com");
        var linkId = await LinkUserAsync(userA.Id, config.Id, subject: "sub-live-1");

        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var external = BuildExternalPrincipal("sub-live-1", "ownerlive@acme.com", "Owner Live");
            var result = await processor.ProcessAsync(external, config.Id, default, authenticatedUserId: userB.Id);
            Assert.False(result.Succeeded);
            Assert.Equal("Idp.LinkedToOtherUser", result.ErrorCode);
        }

        // The live link is untouched — still owned by A, still live.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var link = await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken);
            Assert.NotNull(link);
            Assert.Equal(userA.Id, link!.UserId);
            Assert.False(link.IsUnlinked);
        }
    }

    [Fact]
    public async Task ForgottenLink_ReHomes_To_Different_Authenticated_User()
    {
        // Once A releases the identity (unlink → doc deleted), a DIFFERENT
        // authenticated user B may claim the same (iss,sub) — "unlink forgets the
        // binding", match key is (iss,sub), not the old link id.
        var config = await CreateEnabledEntraConfig();
        var userA = await Factory.CreateTestUserWithIdentityAsync("Owner", "Aaa", "OA", "ownera@acme.com");
        var userB = await Factory.CreateTestUserWithIdentityAsync("Claim", "Bbb", "CB", "claimb@acme.com");
        var linkId = await LinkUserAsync(userA.Id, config.Id, subject: "sub-rehome-1");

        // A unlinks → the terminal event makes the projection drop the doc.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(linkId, new ExternalIdentityUnlinkedEvent(linkId, DateTimeOffset.UtcNow, userA.Id));
            session.Events.Append(userA.Id, new UserExternalIdentityUnlinkedEvent(userA.Id, linkId, config.Id, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var external = BuildExternalPrincipal("sub-rehome-1", "claimb@acme.com", "Claim Bbb");
            var result = await processor.ProcessAsync(external, config.Id, default, authenticatedUserId: userB.Id);
            Assert.True(result.Succeeded);
            Assert.Equal(userB.Id, result.UserId);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var live = await session.Query<ExternalIdentityLink>()
                .Where(l => l.Subject == "sub-rehome-1" && !l.IsUnlinked)
                .FirstOrDefaultAsync();
            Assert.NotNull(live);
            Assert.Equal(userB.Id, live!.UserId);
            Assert.NotEqual(linkId, live.Id);
        }
    }

    [Fact]
    public async Task UnsupportedProtocolType_Returns_TypeNotSupportedError()
    {
        // Callback-flow type-discriminator gate. A LoginProvider whose Type is
        // neither Oidc nor Saml (the two protocols actually wired up) must
        // surface the centralized "type not yet supported" error code — not an
        // NPE on a missing flavor lookup. Saml/Oidc both flow through; Ldap +
        // Kerberos are the remaining unsupported enum values today.
        var id = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<LoginProvider>(id, new LoginProviderAddedEvent(
                Id: id,
                Type: LoginProviderType.Ldap,
                Flavor: "ldap-future",
                Slug: "ldap-future",
                DisplayName: "LDAP Future",
                Description: null,
                IsBuiltIn: false,
                Enabled: true,
                ClientId: string.Empty,
                ClientSecretEncrypted: null,
                Scopes: [],
                UserUpdateScript: string.Empty,
                StoreRawClaims: false,
                RawClaimsRetentionDays: null,
                AutoCreateUsers: false,
                AllowLinking: false,
                TrustForEmailLink: false,
                AllowedEmailDomains: null,
                IconName: null,
                ButtonColorHex: null,
                FlavorData: null,
                CreatedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope2 = Factory.Services.CreateScope();
        var processor = scope2.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var external = BuildExternalPrincipal("sub-anything", "x@y.z", "Some One");
        var result = await processor.ProcessAsync(external, id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("LoginProvider.TypeNotSupported", result.ErrorCode);
    }

    [Fact]
    public async Task MissingSubject_Fails()
    {
        var config = await CreateEnabledEntraConfig();

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("iss", Issuer));
        identity.AddClaim(new Claim("email", "noSub@acme.com"));
        var principal = new ClaimsPrincipal(identity);

        var result = await processor.ProcessAsync(principal, config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.InvalidToken", result.ErrorCode);
    }

    [Fact]
    public async Task DeactivatedUser_WithExternalLink_CannotSignIn()
    {
        // Regression: the admin recycle-bin deactivates a user (IsActive=false) but
        // deliberately keeps their external identity links. External login must
        // refuse a deactivated/deleted user, exactly like password/magic-link/
        // passkey do — otherwise a binned user could re-authenticate via their IdP
        // and bypass the bin.
        var config = await CreateEnabledEntraConfig();
        var user = await Factory.CreateTestUserWithIdentityAsync("Ban", "Ned", "BN", "banned@acme.com");
        await LinkUserAsync(user.Id, config.Id, subject: "sub-banned-1");

        using (var scope = Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            appUser!.IsActive = false;
            await userManager.UpdateAsync(appUser);
        }

        using var scope2 = Factory.Services.CreateScope();
        var processor = scope2.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
        var external = BuildExternalPrincipal(subject: "sub-banned-1", email: "banned@acme.com", name: "Ban Ned");
        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.UserInactive", result.ErrorCode);
    }

    private async Task<LoginProvider> CreateEnabledEntraConfig(
        bool autoCreate = false,
        bool trustForEmailLink = false,
        string[]? allowedDomains = null)
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var flavorData = JsonDocument.Parse("""{"TenantId": "test-tenant"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.EntraId,
            DisplayName: "Test Entra " + Guid.NewGuid().ToString("N")[..6],
            Slug: $"s{Guid.NewGuid():N}"[..12],
            FlavorData: flavorData));
        Assert.False(result.IsError);
        var id = result.Value.Id;

        session.Events.Append(id, new LoginProviderUpdatedEvent(
            Id: id,
            DisplayName: result.Value.DisplayName,
            Description: null,
            ClientId: "client-id-test",
            Scopes: ["openid", "profile", "email"],
            UserUpdateScript: """
                (claims) => ({
                  firstname: claims.given_name?.trim(),
                  lastname: claims.family_name?.trim(),
                  email: claims.email ?? claims.preferred_username,
                  acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
                })
            """,
            StoreRawClaims: true,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: autoCreate,
            AllowLinking: true,
            TrustForEmailLink: trustForEmailLink,
            AllowedEmailDomains: allowedDomains?.ToList(),
            IconName: "microsoft",
            ButtonColorHex: null,
            FlavorData: flavorData,
            UpdatedAt: DateTimeOffset.UtcNow));
        session.Events.Append(id, new LoginProviderEnabledEvent(id, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (await session.LoadAsync<LoginProvider>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task<Guid> LinkUserAsync(Guid userId, Guid loginProviderId, string subject)
    {
        var linkId = Guid.NewGuid();
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<ExternalIdentityLink>(linkId,
            new ExternalIdentityLinkedEvent(
                Id: linkId,
                UserId: userId,
                LoginProviderId: loginProviderId,
                Issuer: Issuer,
                Subject: subject,
                Email: null,
                DisplayName: null,
                LinkedAt: DateTimeOffset.UtcNow));
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            userId, linkId, loginProviderId, Issuer, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return linkId;
    }

    private static ClaimsPrincipal BuildExternalPrincipal(
        string subject,
        string? email,
        string? name,
        IReadOnlyList<string>? groups = null,
        IReadOnlyList<string>? amr = null,
        bool emailVerified = true)
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("iss", Issuer));
        identity.AddClaim(new Claim("sub", subject));
        if (email is not null)
        {
            identity.AddClaim(new Claim("email", email));
            // OIDC standard email_verified flag — a properly-configured IdP
            // asserts it. Audit H3: TrustForEmailLink requires it to be true
            // before auto-linking by email. Default true here so the common
            // "verified IdP" tests exercise the happy path.
            identity.AddClaim(new Claim("email_verified", emailVerified ? "true" : "false"));
        }
        if (name is not null)
        {
            identity.AddClaim(new Claim("name", name));
            // Also emit given_name / family_name so user-update scripts using
            // the default OIDC shape can split the name without extra work.
            var parts = name.Split(' ', 2);
            identity.AddClaim(new Claim("given_name", parts[0]));
            if (parts.Length > 1)
                identity.AddClaim(new Claim("family_name", parts[1]));
        }
        if (groups is not null)
            foreach (var g in groups)
                identity.AddClaim(new Claim("groups", g));
        if (amr is not null)
            foreach (var a in amr)
                identity.AddClaim(new Claim("amr", a));
        return new ClaimsPrincipal(identity);
    }
}
