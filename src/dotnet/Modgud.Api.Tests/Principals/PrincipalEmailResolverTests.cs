using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authorization.Principals;
using Modgud.Authentication.Identity;

namespace Modgud.Api.Tests.Principals;

[Collection(IntegrationTestCollection.Name)]
public class PrincipalEmailResolverTests : IntegrationTestBase
{
    public PrincipalEmailResolverTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Human_WithEmail_ReturnsSingleAddress()
    {
        var user = await Factory.CreateTestUserAsync("Alice", "Mail", "AM", "alice@test.com");

        using var scope = Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPrincipalEmailResolver>();

        var emails = await resolver.ResolveEmailsAsync(user.Id);

        Assert.Single(emails);
        Assert.Equal("alice@test.com", emails[0]);
    }

    [Fact]
    public async Task Group_SharedMode_ReturnsGroupEmail()
    {
        var group = await CreateGroupWithEmailAsync("SharedTeam", "team@acme.com", EmailMode.Shared, memberIds: []);

        using var scope = Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPrincipalEmailResolver>();

        var emails = await resolver.ResolveEmailsAsync(group.Id);

        Assert.Single(emails);
        Assert.Equal("team@acme.com", emails[0]);
    }

    [Fact]
    public async Task Group_ExpandMode_ResolvesMemberEmails()
    {
        var alice = await Factory.CreateTestUserAsync("Alice", "A", "AA", "alice@acme.com");
        var bob = await Factory.CreateTestUserAsync("Bob", "B", "BB", "bob@acme.com");

        var group = await CreateGroupWithEmailAsync("ExpandTeam", email: null, EmailMode.ExpandToMembers,
            memberIds: [alice.Id, bob.Id]);

        using var scope = Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPrincipalEmailResolver>();

        var emails = await resolver.ResolveEmailsAsync(group.Id);

        Assert.Equal(2, emails.Count);
        Assert.Contains("alice@acme.com", emails);
        Assert.Contains("bob@acme.com", emails);
    }

    [Fact]
    public async Task NestedGroup_ExpandMode_ResolvesTransitiveMemberEmails()
    {
        var alice = await Factory.CreateTestUserAsync("Alice", "A", "AA", "alice@acme.com");
        var bob = await Factory.CreateTestUserAsync("Bob", "B", "BB", "bob@acme.com");

        var innerGroup = await CreateGroupWithEmailAsync("Inner", null, EmailMode.ExpandToMembers, [alice.Id]);
        var outerGroup = await CreateGroupWithEmailAsync("Outer", null, EmailMode.ExpandToMembers, [innerGroup.Id, bob.Id]);

        using var scope = Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPrincipalEmailResolver>();

        var emails = await resolver.ResolveEmailsAsync(outerGroup.Id);

        Assert.Equal(2, emails.Count);
        Assert.Contains("alice@acme.com", emails);
        Assert.Contains("bob@acme.com", emails);
    }

    [Fact]
    public async Task Group_SharedMode_WithoutEmail_FallsBackToExpand()
    {
        var alice = await Factory.CreateTestUserAsync("Alice", "Fallback", "AF", "alice@test.com");

        var group = await CreateGroupWithEmailAsync("NoEmailShared", email: null, EmailMode.Shared, [alice.Id]);

        using var scope = Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPrincipalEmailResolver>();

        var emails = await resolver.ResolveEmailsAsync(group.Id);

        Assert.Single(emails);
        Assert.Equal("alice@test.com", emails[0]);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<Group> CreateGroupWithEmailAsync(
        string name, string? email, EmailMode emailMode, List<Guid> memberIds)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // PrincipalProjection (inline) builds the Group doc from
        // GroupCreatedEvent — direct Store conflicts under Marten 8.34+.
        var group = new Group
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            MemberIds = memberIds,
            Email = email,
            EmailMode = emailMode,
        };
        session.Events.StartStream(group.Id,
            new GroupCreatedEvent(group.Id, group.Name, group.Description,
                group.MemberIds, group.RoleIds,
                group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                group.MembershipScriptDependencies, group.Email, group.EmailMode));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return group;
    }
}
