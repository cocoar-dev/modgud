using Cocoar.Auth.Infrastructure.Events;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Events;

/// <summary>
/// Pins the wire-shape of <see cref="UserViewSignalRDispatch"/> messages.
/// Wolverine routes them by record-type / property positions; renaming or
/// reordering silently breaks SignalR fan-out to the admin UI.
/// </summary>
public class SignalRSideEffectMessagesTests
{
    public class UserViewSignalRDispatchRecord
    {
        [Fact]
        public void Carries_action_view_and_id()
        {
            var id = Guid.NewGuid();
            var view = new UserView { Id = id, UserName = "alice" };

            var msg = new UserViewSignalRDispatch(SignalRDispatchAction.Updated, view, id);

            Assert.Equal(SignalRDispatchAction.Updated, msg.Action);
            Assert.Same(view, msg.View);
            Assert.Equal(id, msg.Id);
        }

        [Fact]
        public void View_can_be_null_for_deleted_messages()
        {
            // Deleted messages don't need a payload — only the id matters.
            var id = Guid.NewGuid();
            var msg = new UserViewSignalRDispatch(SignalRDispatchAction.Deleted, null, id);

            Assert.Null(msg.View);
            Assert.Equal(SignalRDispatchAction.Deleted, msg.Action);
            Assert.Equal(id, msg.Id);
        }

        [Fact]
        public void Two_dispatches_with_same_payload_are_value_equal()
        {
            var id = Guid.NewGuid();
            var view = new UserView { Id = id };
            var a = new UserViewSignalRDispatch(SignalRDispatchAction.Created, view, id);
            var b = new UserViewSignalRDispatch(SignalRDispatchAction.Created, view, id);
            Assert.Equal(a, b);
        }
    }

    public class SignalRDispatchActionEnum
    {
        // Pin the integer values — they're effectively a public contract once
        // any persisted message stream uses them.
        [Theory]
        [InlineData(SignalRDispatchAction.Created, 0)]
        [InlineData(SignalRDispatchAction.Updated, 1)]
        [InlineData(SignalRDispatchAction.Deleted, 2)]
        public void Has_stable_integer_value(SignalRDispatchAction action, int expected)
        {
            Assert.Equal(expected, (int)action);
        }
    }
}
