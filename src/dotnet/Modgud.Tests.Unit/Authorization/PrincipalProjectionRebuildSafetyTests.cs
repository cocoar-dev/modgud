using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Events;
using Modgud.Authentication.Projections;
using Modgud.Authorization.Events;
using Modgud.Authorization.Projections;
using Modgud.Domain.Users.Events;

namespace Modgud.Tests.Unit.Authorization;

public class PrincipalProjectionRebuildSafetyTests
{
    [Fact]
    public void Shared_table_projections_disable_destructive_marten_teardown()
    {
        Assert.False(new PersonProjection().Options.TeardownDataOnRebuild);
        Assert.False(new GroupProjection().Options.TeardownDataOnRebuild);
    }

    [Fact]
    public void Explicit_constructors_keep_complete_event_allow_lists()
    {
        var personProjection = new PersonProjection();
        Type[] expectedPersonEvents =
        [
            typeof(UserCreatedEvent),
            typeof(UserMigratedEvent),
            typeof(UserUpdatedEvent),
            typeof(UserIdentitySetupEvent),
            typeof(UserUserNameChangedEvent),
            typeof(UserActivatedEvent),
            typeof(UserDeactivatedEvent),
            typeof(UserDeletedEvent),
            typeof(UserExternalIdentityLinkedEvent),
            typeof(UserExternalIdentityUnlinkedEvent),
        ];

        var groupProjection = new GroupProjection();
        Type[] expectedGroupEvents =
        [
            typeof(GroupCreatedEvent),
            typeof(GroupUpdatedEvent),
            typeof(GroupMembershipRecomputedEvent),
            typeof(GroupMembershipRecomputeFailedEvent),
            typeof(GroupDeletedEvent),
        ];

        Assert.Equal(
            expectedPersonEvents.OrderBy(type => type.FullName),
            personProjection.IncludedEventTypes.OrderBy(type => type.FullName));
        Assert.Equal(
            expectedGroupEvents.OrderBy(type => type.FullName),
            groupProjection.IncludedEventTypes.OrderBy(type => type.FullName));
    }
}
