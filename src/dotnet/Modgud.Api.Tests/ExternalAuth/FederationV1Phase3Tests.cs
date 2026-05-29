using System.Security.Claims;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Domain.Users.Events;
using Modgud.Permissions.Abstractions;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// Federation v1 — Phase 3 (login wiring: deriver bake-in + profile gate).
/// First behavior activation. Pins: a TrustForAuthorization provider bakes the
/// internal session-group claim for matched ExternallyDrivable groups; an
/// untrusted (or non-matching) login bakes none; and the AuthoritativeForProfile
/// gate (with the JIT-creator default) replaces the every-provider flapping.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FederationV1Phase3Tests : IntegrationTestBase
{
    public FederationV1Phase3Tests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Issuer = "https://idp.phase3.test/v2.0";
    private const string GroupScript =
        "(p) => Type.Is(p, 'person') && p.IsActive && p.ExternalGroups.includes('entra-admins')";

    [Fact]
    public async Task TrustedProvider_Bakes_SessionGroup_Claim_For_Matched_Group()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateOidcProviderAsync(trustForAuthorization: true);
        var user = await Factory.CreateTestUserWithIdentityAsync("Fed", "Trusted", "FT", "fed-trusted@acme.com");
        await LinkUserAsync(user.Id, config.Id, "sub-trusted");
        var group = await CreateExternallyDrivableGroupAsync(GroupScript);

        var result = await RunLoginAsync(config.Id, "sub-trusted", "fed-trusted@acme.com", ["entra-admins"]);

        Assert.True(result.Succeeded);
        var sessionGroups = result.Principal!.FindAll(FederationClaimTypes.SessionGroup).Select(c => c.Value).ToList();
        Assert.Contains(group.ToString(), sessionGroups);
    }

    [Fact]
    public async Task UntrustedProvider_Bakes_No_SessionGroup_Claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateOidcProviderAsync(trustForAuthorization: false);
        var user = await Factory.CreateTestUserWithIdentityAsync("Fed", "Untrusted", "FU", "fed-untrusted@acme.com");
        await LinkUserAsync(user.Id, config.Id, "sub-untrusted");
        await CreateExternallyDrivableGroupAsync(GroupScript);

        var result = await RunLoginAsync(config.Id, "sub-untrusted", "fed-untrusted@acme.com", ["entra-admins"]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Principal!.FindAll(FederationClaimTypes.SessionGroup));
    }

    [Fact]
    public async Task TrustedProvider_NonMatching_Groups_Bakes_No_Claim()
    {
        var config = await CreateOidcProviderAsync(trustForAuthorization: true);
        var user = await Factory.CreateTestUserWithIdentityAsync("Fed", "Nomatch", "FN", "fed-nomatch@acme.com");
        await LinkUserAsync(user.Id, config.Id, "sub-nomatch");
        await CreateExternallyDrivableGroupAsync(GroupScript);

        var result = await RunLoginAsync(config.Id, "sub-nomatch", "fed-nomatch@acme.com", ["all-staff"]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Principal!.FindAll(FederationClaimTypes.SessionGroup));
    }

    [Fact]
    public async Task ProfileGate_NonAuthoritative_NonCreator_Does_Not_Patch_But_Creator_Does()
    {
        var ct = TestContext.Current.CancellationToken;
        // A non-authoritative provider whose script would rename the user.
        var config = await CreateOidcProviderAsync(
            trustForAuthorization: false,
            userUpdateScript: "(claims) => ({ firstname: claims.given_name })");

        // User A: linked as a NON-creator → must NOT be patched (no flapping).
        var userA = await Factory.CreateTestUserWithIdentityAsync("Original", "A", "OA", "gate-a@acme.com");
        await LinkUserAsync(userA.Id, config.Id, "sub-gate-a", isCreator: false);
        await RunLoginAsync(config.Id, "sub-gate-a", "gate-a@acme.com", [], givenName: "Changed");

        // User B: linked as the JIT CREATOR → stays profile-authoritative by default.
        var userB = await Factory.CreateTestUserWithIdentityAsync("Original", "B", "OB", "gate-b@acme.com");
        await LinkUserAsync(userB.Id, config.Id, "sub-gate-b", isCreator: true);
        await RunLoginAsync(config.Id, "sub-gate-b", "gate-b@acme.com", [], givenName: "Changed");

        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var a = await users.FindByIdAsync(userA.Id.ToString());
        var b = await users.FindByIdAsync(userB.Id.ToString());
        Assert.Equal("Original", a!.Firstname); // gated off — not authoritative, not creator
        Assert.Equal("Changed", b!.Firstname);  // creator default authority patches
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<ExternalLoginResult> RunLoginAsync(
        Guid providerId, string subject, string email, IReadOnlyList<string> groups, string? givenName = null)
    {
        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
        return await processor.ProcessAsync(
            BuildPrincipal(subject, email, groups, givenName), providerId, TestContext.Current.CancellationToken);
    }

    private async Task<LoginProvider> CreateOidcProviderAsync(
        bool trustForAuthorization,
        string userUpdateScript = "(claims) => ({ email: claims.email })")
    {
        var id = Guid.NewGuid();
        var slug = $"fed{Guid.NewGuid():N}"[..12];
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<LoginProvider>(id, new LoginProviderAddedEvent(
            Id: id, Type: LoginProviderType.Oidc, Flavor: LoginProviderFlavor.GenericOidc,
            Slug: slug, DisplayName: $"Fed_{Guid.NewGuid():N}"[..12], Description: null,
            IsBuiltIn: false, Enabled: true, ClientId: "client", ClientSecretEncrypted: null,
            Scopes: ["openid", "profile", "email"], UserUpdateScript: userUpdateScript,
            StoreRawClaims: false, RawClaimsRetentionDays: null,
            AutoCreateUsers: false, AllowLinking: true, TrustForEmailLink: false,
            AllowedEmailDomains: null, IconName: null, ButtonColorHex: null, FlavorData: null,
            CreatedAt: DateTimeOffset.UtcNow,
            TrustForAuthorization: trustForAuthorization, AuthoritativeForProfile: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<LoginProvider>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task LinkUserAsync(Guid userId, Guid providerId, string subject, bool isCreator = false)
    {
        var linkId = Guid.NewGuid();
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<ExternalIdentityLink>(linkId, new ExternalIdentityLinkedEvent(
            linkId, userId, providerId, Issuer, subject, null, null, DateTimeOffset.UtcNow, IsCreator: isCreator));
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            userId, linkId, providerId, Issuer, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateExternallyDrivableGroupAsync(string script)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var compiled = evaluator.TranspileMembershipScript(script);
        var id = Guid.CreateVersion7();
        session.Events.StartStream(id, new GroupCreatedEvent(
            id, $"Fed_{Guid.NewGuid():N}", null, [], [],
            MembershipMode.Auto, script, compiled, null, null, EmailMode.Shared,
            [AppSlugs.Modgud], ExternallyDrivable: true));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static ClaimsPrincipal BuildPrincipal(
        string subject, string email, IReadOnlyList<string> groups, string? givenName)
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("iss", Issuer));
        identity.AddClaim(new Claim("sub", subject));
        identity.AddClaim(new Claim("email", email));
        if (givenName is not null) identity.AddClaim(new Claim("given_name", givenName));
        foreach (var g in groups) identity.AddClaim(new Claim("groups", g));
        return new ClaimsPrincipal(identity);
    }
}
