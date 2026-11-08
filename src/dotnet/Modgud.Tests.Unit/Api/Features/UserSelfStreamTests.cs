using System.Reactive.Linq;
using System.Reactive.Subjects;
using BuildingBlocks.EventDispatcher;
using Modgud.Api.Features.Users;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Tests.Unit.Api.Features;

/// <summary>
/// B4-AP-06 — the profile page relied on the UserActions stream, which needs user:read, so a
/// plain user never saw their own profile change (an approval, an admin edit) live. The self
/// stream carries only the caller's own user events, in the caller's realm.
/// </summary>
public class UserSelfStreamTests
{
    [Fact]
    public async Task Only_the_callers_own_user_events_of_their_realm_pass()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var source = new Subject<DataEvent>();
        var received = new List<DataEvent>();
        using var _ = UserHub.SelfEvents(source, "acme", me).Subscribe(received.Add);

        source.OnNext(new DataEvent(DataEventAction.Updated, "User", [new UserView { Id = me }]).WithTenant("acme"));
        source.OnNext(new DataEvent(DataEventAction.Updated, "User", [new UserView { Id = other }]).WithTenant("acme"));
        source.OnNext(new DataEvent(DataEventAction.Updated, "User", [new UserView { Id = me }]).WithTenant("beta"));
        source.OnNext(new DataEvent(DataEventAction.Updated, "Group", [new UserView { Id = me }]).WithTenant("acme"));
        await Task.Yield();

        var only = Assert.Single(received);
        Assert.Single(only.Payload); // no other user's data rides along
    }

    [Fact]
    public void Without_a_caller_nothing_passes()
    {
        var source = new Subject<DataEvent>();
        var received = new List<DataEvent>();
        using var _ = UserHub.SelfEvents(source, "acme", null).Subscribe(received.Add);

        source.OnNext(new DataEvent(DataEventAction.Updated, "User", [new UserView { Id = Guid.NewGuid() }]).WithTenant("acme"));

        Assert.Empty(received);
    }
}
