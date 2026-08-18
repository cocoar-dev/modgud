using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;

namespace Modgud.Tests.Unit.Authorization.Apps;

public class ApplicationScopeResolverTests
{
    private static readonly App AlertHub = new()
    {
        Id = Guid.NewGuid(),
        Slug = "alert-hub",
        DisplayName = "AlertHub",
    };

    [Fact]
    public void Bound_root_includes_every_principal_type_and_nested_members()
    {
        var person = new Person { Id = Guid.NewGuid(), AccountName = "alice" };
        var position = new PositionPrincipal { Id = Guid.NewGuid(), AccountName = "gate" };
        var serviceAccount = new ServiceAccount { Id = Guid.NewGuid(), AccountName = "sync" };
        var nested = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Nested",
            MemberIds = [position.Id, serviceAccount.Id],
        };
        var root = new Group
        {
            Id = Guid.NewGuid(),
            Name = "AlertHub principals",
            BoundTo = [AlertHub.Slug],
            MemberIds = [person.Id, nested.Id],
            RoleIds = [],
        };
        var unrelated = new Person { Id = Guid.NewGuid(), AccountName = "bob" };

        var result = ApplicationScopeResolver.BuildSnapshot(
            AlertHub,
            [person, position, serviceAccount, nested, root, unrelated]);

        Assert.Equal([root.Id], result.RootGroups.Select(g => g.Id));
        Assert.Equal(
            new HashSet<Guid> { root.Id, nested.Id, person.Id, position.Id, serviceAccount.Id },
            result.Principals.Select(p => p.Id).ToHashSet());
        Assert.DoesNotContain(result.Principals, p => p.Id == unrelated.Id);
    }

    [Fact]
    public void Wildcard_is_a_root_and_inactive_or_deleted_members_are_excluded()
    {
        var active = new Person { Id = Guid.NewGuid(), AccountName = "active" };
        var inactive = new Person { Id = Guid.NewGuid(), AccountName = "inactive", IsActive = false };
        var deleted = new ServiceAccount { Id = Guid.NewGuid(), AccountName = "deleted", IsDeleted = true };
        var wildcard = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Every app",
            BoundTo = ["*"],
            MemberIds = [active.Id, inactive.Id, deleted.Id],
        };

        var result = ApplicationScopeResolver.BuildSnapshot(
            AlertHub,
            [wildcard, active, inactive, deleted]);

        Assert.Contains(result.Principals, p => p.Id == wildcard.Id);
        Assert.Contains(result.Principals, p => p.Id == active.Id);
        Assert.DoesNotContain(result.Principals, p => p.Id == inactive.Id);
        Assert.DoesNotContain(result.Principals, p => p.Id == deleted.Id);
    }

    [Fact]
    public void Group_without_roles_still_defines_the_scope()
    {
        var root = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Visibility only",
            BoundTo = [AlertHub.Slug],
            RoleIds = [],
        };

        var result = ApplicationScopeResolver.BuildSnapshot(AlertHub, [root]);

        Assert.Single(result.RootGroups);
        Assert.Single(result.Principals);
    }

    [Fact]
    public void Version_changes_with_roots_but_not_with_membership()
    {
        var memberA = new Person { Id = Guid.NewGuid(), AccountName = "a" };
        var memberB = new Person { Id = Guid.NewGuid(), AccountName = "b" };
        var rootA = new Group
        {
            Id = Guid.NewGuid(),
            Name = "A",
            BoundTo = [AlertHub.Slug],
            MemberIds = [memberA.Id],
        };

        var before = ApplicationScopeResolver.BuildSnapshot(AlertHub, [rootA, memberA]);
        rootA.MemberIds = [memberA.Id, memberB.Id];
        var membershipChanged = ApplicationScopeResolver.BuildSnapshot(AlertHub, [rootA, memberA, memberB]);

        var rootB = new Group
        {
            Id = Guid.NewGuid(),
            Name = "B",
            BoundTo = [AlertHub.Slug],
        };
        var definitionChanged = ApplicationScopeResolver.BuildSnapshot(
            AlertHub,
            [rootA, rootB, memberA, memberB]);

        Assert.Equal(before.ScopeVersion, membershipChanged.ScopeVersion);
        Assert.NotEqual(before.ScopeVersion, definitionChanged.ScopeVersion);
    }

    [Fact]
    public void Version_is_independent_of_root_order()
    {
        var first = new Group { Id = Guid.NewGuid(), BoundTo = [AlertHub.Slug] };
        var second = new Group { Id = Guid.NewGuid(), BoundTo = [AlertHub.Slug] };

        Assert.Equal(
            ApplicationScopeResolver.BuildSnapshot(AlertHub, [first, second]).ScopeVersion,
            ApplicationScopeResolver.BuildSnapshot(AlertHub, [second, first]).ScopeVersion);
    }

    [Fact]
    public void Version_changes_when_the_nested_group_definition_changes()
    {
        var nested = new Group { Id = Guid.NewGuid(), Name = "Nested" };
        var root = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Root",
            BoundTo = [AlertHub.Slug],
        };
        var before = ApplicationScopeResolver.BuildSnapshot(AlertHub, [root, nested]);

        root.MemberIds = [nested.Id];
        var after = ApplicationScopeResolver.BuildSnapshot(AlertHub, [root, nested]);

        Assert.NotEqual(before.ScopeVersion, after.ScopeVersion);
    }
}
