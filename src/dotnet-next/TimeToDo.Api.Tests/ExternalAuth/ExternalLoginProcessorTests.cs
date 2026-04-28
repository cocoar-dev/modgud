using System.Security.Claims;
using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Authentication.Api.Admin.IdentityProviders.Commands;
using TimeToDo.Authentication.Api.ExternalAuth;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Domain;
using TimeToDo.Authentication.Domain.ExternalAuth;
using TimeToDo.Authentication.Domain.ExternalAuth.Events;
using TimeToDo.Authorization.Principals;

using Wolverine;

namespace TimeToDo.Api.Tests.ExternalAuth;

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
        Assert.Equal(Issuer, principal.FindFirst("timetodo.external.issuer")?.Value);
        Assert.Empty(principal.FindAll("timetodo.external.group"));

        // Script-run snapshot was persisted on the link (debugging artifact).
        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var link = await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken);
        Assert.NotNull(link!.LastScriptOutput);
        Assert.True(link.LastScriptSucceeded);
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
        var principal = await session.LoadAsync<TimeToDo.Authorization.Principals.Person>(user.Id, TestContext.Current.CancellationToken);
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
    public async Task UnlinkedLink_Fails()
    {
        var config = await CreateEnabledEntraConfig();
        var user = await Factory.CreateTestUserWithIdentityAsync("U", "Nlink", "UN", "un@acme.com");
        var linkId = await LinkUserAsync(user.Id, config.Id, subject: "sub-un-1");

        // Unlink
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(linkId, new ExternalIdentityUnlinkedEvent(linkId, DateTimeOffset.UtcNow, user.Id));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var external = BuildExternalPrincipal("sub-un-1", "un@acme.com", "U Nlink");
            var result = await processor.ProcessAsync(external, config.Id, default);
            Assert.False(result.Succeeded);
            Assert.Equal("Idp.Unlinked", result.ErrorCode);
        }
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

    private async Task<IdpConfig> CreateEnabledEntraConfig(
        bool autoCreate = false,
        bool trustForEmailLink = false,
        string[]? allowedDomains = null)
    {
        using var scope = Factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var flavorData = JsonDocument.Parse("""{"TenantId": "test-tenant"}""");
        var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(new CreateIdpConfigCommand(
            Flavor: IdpFlavor.EntraId,
            DisplayName: "Test Entra " + Guid.NewGuid().ToString("N")[..6],
            FlavorData: flavorData));
        Assert.False(result.IsError);
        var id = result.Value.Id;

        session.Events.Append(id, new IdpConfigUpdatedEvent(
            Id: id,
            DisplayName: result.Value.DisplayName,
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
        session.Events.Append(id, new IdpConfigEnabledEvent(id, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (await session.LoadAsync<IdpConfig>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task<Guid> LinkUserAsync(Guid userId, Guid idpConfigId, string subject)
    {
        var linkId = Guid.NewGuid();
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<ExternalIdentityLink>(linkId,
            new ExternalIdentityLinkedEvent(
                Id: linkId,
                UserId: userId,
                IdpConfigId: idpConfigId,
                Issuer: Issuer,
                Subject: subject,
                Email: null,
                DisplayName: null,
                LinkedAt: DateTimeOffset.UtcNow));
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            userId, linkId, idpConfigId, Issuer, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return linkId;
    }

    private static ClaimsPrincipal BuildExternalPrincipal(
        string subject,
        string? email,
        string? name,
        IReadOnlyList<string>? groups = null,
        IReadOnlyList<string>? amr = null)
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("iss", Issuer));
        identity.AddClaim(new Claim("sub", subject));
        if (email is not null) identity.AddClaim(new Claim("email", email));
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
