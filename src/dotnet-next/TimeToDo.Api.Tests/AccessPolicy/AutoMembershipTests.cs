using Marten;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api.Features.Groups;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Domain;
using TimeToDo.Authentication.Events;
using TimeToDo.Authorization.Principals;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Authorization.Membership;


namespace TimeToDo.Api.Tests.AccessPolicy;

[Collection(IntegrationTestCollection.Name)]
public class AutoMembershipTests : IntegrationTestBase
{
    public AutoMembershipTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Evaluator_BuildsPredicate_ThatMatchesExpectedUsers()
    {
        using var scope = Factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();

        var compiled = evaluator.TranspileMembershipScript(
            "(u) => u.IsActive && u.Email.endsWith('@acme.com')");
        var predicate = evaluator.BuildPredicate<Person>(compiled).Compile();

        var match = predicate(new Person
        {
            Id = Guid.NewGuid(),
            Email = "alice@acme.com",
            IsActive = true
        });
        var noMatch = predicate(new Person
        {
            Id = Guid.NewGuid(),
            Email = "bob@other.com",
            IsActive = true
        });

        Assert.True(match);
        Assert.False(noMatch);
    }

    [Fact]
    public async Task Evaluator_ScriptError_BubblesUpException()
    {
        using var scope = Factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();

        // Dynamic/unsafe property access — translator can't handle. BuildPredicate must
        // throw so the recalculator can emit GroupMembershipRecomputeFailedEvent.
        var compiled = evaluator.TranspileMembershipScript("(u) => u['doesNotExist']");

        Assert.Throws<InvalidOperationException>(() => evaluator.BuildPredicate<Person>(compiled));
    }

    [Fact]
    public async Task RecalculateForGroup_OptionalChaining_WorksAfterTypeNarrowing()
    {
        var aliceMatch = await Factory.CreateTestUserWithIdentityAsync(
            "Anna", "Opt", "AO", email: "anna@x.com", password: "TestPass1234", permissions: []);
        var bobNoMatch = await Factory.CreateTestUserWithIdentityAsync(
            "Bob", "Opt", "BO2", email: "bob@x.com", password: "TestPass1234", permissions: []);

        // Use ?. — this was broken in beta.2 (VisitChain lost the narrowing context)
        var groupId = await CreateAutoGroupAsync(
            "OptChain",
            "(u) => Type.Is(u, 'person') && u.IsActive && (u.Firstname?.startsWith('A') || u.Firstname?.startsWith('L'))");
        await RunRecalculateForGroupAsync(groupId);

        var group = await Factory.GetDocumentAsync<Group>(groupId);
        Assert.NotNull(group);
        Assert.Null(group!.MembershipLastError);
        Assert.Contains(aliceMatch.Id, group.MemberIds);
        Assert.DoesNotContain(bobNoMatch.Id, group.MemberIds);
    }

    [Fact]
    public async Task RecalculateForGroup_AddsMatchingUsersAsMembers()
    {
        var userMatch = await Factory.CreateTestUserWithIdentityAsync(
            "Alice", "Match", "AM", email: "alice@acme.com", password: "TestPass1234", permissions: []);
        var userNoMatch = await Factory.CreateTestUserWithIdentityAsync(
            "Bob", "Other", "BO", email: "bob@other.com", password: "TestPass1234", permissions: []);

        var groupId = await CreateAutoGroupAsync("AcmeUsers", "(u) => Type.Is(u, 'person') && u.Email.endsWith('@acme.com')");
        await RunRecalculateForGroupAsync(groupId);

        var group = await Factory.GetDocumentAsync<Group>(groupId);
        Assert.NotNull(group);
        Assert.Contains(userMatch.Id, group!.MemberIds);
        Assert.DoesNotContain(userNoMatch.Id, group.MemberIds);
    }

    [Fact]
    public async Task RecalculateForUser_AddsUserToMatchingGroups_AndRemovesFromNonMatching()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Charlie", "Active", "CA", email: "charlie@acme.com", password: "TestPass1234", permissions: []);

        var matchGroupId = await CreateAutoGroupAsync("Matching", "(u) => Type.Is(u, 'person') && u.Email.endsWith('@acme.com')");
        var nonMatchGroupId = await CreateAutoGroupAsync("NonMatching", "(u) => Type.Is(u, 'person') && u.Email.endsWith('@other.com')");

        await RunRecalculateForUserAsync(user.Id);

        var matchGroup = await Factory.GetDocumentAsync<Group>(matchGroupId);
        var nonMatchGroup = await Factory.GetDocumentAsync<Group>(nonMatchGroupId);

        Assert.Contains(user.Id, matchGroup!.MemberIds);
        Assert.DoesNotContain(user.Id, nonMatchGroup!.MemberIds);
    }

    [Fact]
    public async Task RecalculateForUser_InactiveUser_RemovedFromGroup()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Dora", "Deactivated", "DD", email: "dora@acme.com", password: "TestPass1234", permissions: []);

        var groupId = await CreateAutoGroupAsync(
            "ActiveOnly", "(u) => Type.Is(u, 'person') && u.IsActive && u.Email.endsWith('@acme.com')");
        await RunRecalculateForGroupAsync(groupId);

        var groupAfterInitial = await Factory.GetDocumentAsync<Group>(groupId);
        Assert.Contains(user.Id, groupAfterInitial!.MemberIds);

        await DeactivateUserAsync(user.Id);
        await RunRecalculateForUserAsync(user.Id);

        var groupAfterDeactivation = await Factory.GetDocumentAsync<Group>(groupId);
        Assert.DoesNotContain(user.Id, groupAfterDeactivation!.MemberIds);
    }

    [Fact]
    public async Task RecalculateForGroup_ManualGroup_IsIgnored()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Eve", "Manual", "EM", email: "eve@acme.com", password: "TestPass1234", permissions: []);

        var manualGroup = await Factory.CreateTestGroupAsync("ManualGroup", [user.Id]);
        await RunRecalculateForUserAsync(user.Id);

        var group = await Factory.GetDocumentAsync<Group>(manualGroup.Id);
        Assert.Contains(user.Id, group!.MemberIds);
    }

    [Fact]
    public async Task RemoveUserFromAllAutoGroups_RemovesAcrossMultipleGroups()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Fred", "Deleted", "FD", email: "fred@acme.com", password: "TestPass1234", permissions: []);

        var groupA = await CreateAutoGroupAsync("A", "(u) => Type.Is(u, 'person') && u.Email.endsWith('@acme.com')");
        var groupB = await CreateAutoGroupAsync("B", "(u) => u.IsActive");
        await RunRecalculateForUserAsync(user.Id);

        Assert.Contains(user.Id, (await Factory.GetDocumentAsync<Group>(groupA))!.MemberIds);
        Assert.Contains(user.Id, (await Factory.GetDocumentAsync<Group>(groupB))!.MemberIds);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
        await recalc.RemoveUserFromAllAutoGroupsAsync(user.Id, session);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(user.Id, (await Factory.GetDocumentAsync<Group>(groupA))!.MemberIds);
        Assert.DoesNotContain(user.Id, (await Factory.GetDocumentAsync<Group>(groupB))!.MemberIds);
    }

    // ── OR-Intersection (Type.Is(u,'person') || Type.Is(u,'group')) ──────

    /// <summary>
    /// Verifies that Marten can translate the expression emitted by
    /// TryResolveViaIntersection: Convert(u, Person).Email after an OR of two
    /// TypeIs nodes. In our model Email lives on Person and Group but NOT on
    /// Principal, so this exercises the convert-cast path in the LINQ provider.
    /// </summary>
    [Fact]
    public async Task RecalculateForGroup_OrIntersection_EmailOnPersonAndGroup_ExcludesServiceAccount()
    {
        // Persons
        var aliceMatch = await Factory.CreateTestUserWithIdentityAsync(
            "Alice", "X", "AX", email: "alice@x.com", password: "TestPass1234", permissions: []);
        var bobNoMatch = await Factory.CreateTestUserWithIdentityAsync(
            "Bob", "Y", "BY", email: "bob@y.com", password: "TestPass1234", permissions: []);

        // Groups — created via events so we can set Email
        var teamMatch = await CreateManualGroupWithEmailAsync("Team X", "team@x.com");
        var otherNoMatch = await CreateManualGroupWithEmailAsync("Other Y", "other@y.com");

        // Auto group: both Person and Group qualify via Email — ServiceAccount has no Email
        var autoGroupId = await CreateAutoGroupAsync(
            "EmailDomain",
            "(u) => (Type.Is(u, 'person') || Type.Is(u, 'group')) && u.Email.endsWith('@x.com')");

        await RunRecalculateForGroupAsync(autoGroupId);

        var group = await Factory.GetDocumentAsync<Group>(autoGroupId);
        Assert.NotNull(group);
        Assert.Null(group!.MembershipLastError);

        // Matches
        Assert.Contains(aliceMatch.Id, group.MemberIds);
        Assert.Contains(teamMatch, group.MemberIds);

        // Non-matches
        Assert.DoesNotContain(bobNoMatch.Id, group.MemberIds);
        Assert.DoesNotContain(otherNoMatch, group.MemberIds);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private async Task<Guid> CreateManualGroupWithEmailAsync(string name, string email)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.CreateVersion7();
        session.Events.StartStream(id, new GroupCreatedEvent(
            id, name, null, [], [], [],
            MembershipMode.Manual, null, null, null, email));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> CreateAutoGroupAsync(string name, string typeScriptArrowFunction)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();

        var compiled = evaluator.TranspileMembershipScript(typeScriptArrowFunction);
        var id = Guid.CreateVersion7();

        session.Events.StartStream(id, new GroupCreatedEvent(
            id, name, null, [], [], [],
            MembershipMode.Auto, typeScriptArrowFunction, compiled));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task RunRecalculateForGroupAsync(Guid groupId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
        var group = await session.LoadAsync<Group>(groupId, TestContext.Current.CancellationToken);
        await recalc.RecalculateForGroupAsync(group!, session);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task RunRecalculateForUserAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
        await recalc.RecalculateForPrincipalAsync(userId, session);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task DeactivateUserAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(userId, new UserDeactivatedEvent(userId));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
