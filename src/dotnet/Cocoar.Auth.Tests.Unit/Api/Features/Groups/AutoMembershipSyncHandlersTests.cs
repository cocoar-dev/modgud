using Cocoar.Auth.Api.Features.Groups;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authorization.Access;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Domain.Users.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cocoar.Auth.Tests.Unit.Api.Features.Groups;

/// <summary>
/// Pinning tests for the dependency-driven auto-membership pipeline. Two halves:
/// <list type="bullet">
///   <item>The path constants in <see cref="PrincipalPaths"/> must keep matching
///         the prefix MembershipEvaluator's dependency collector emits — drift
///         would silently disable the dependency-skip optimisation.</item>
///   <item>The <c>ShouldSync</c> early-out for each handler decides whether the
///         expensive recalc happens — testing it via subclasses keeps us honest
///         even though the actual SyncAsync path requires a Marten session.</item>
/// </list>
/// </summary>
public class AutoMembershipSyncHandlersTests
{
    /// <summary>
    /// Test-only recalculator stub; throws on every call so any test that exercises
    /// the SyncAsync path (which it shouldn't here — we only invoke ShouldSync via
    /// our test subclasses) fails loudly instead of silently no-op'ing.
    /// </summary>
    private sealed class ThrowingRecalculator : IAutoMembershipRecalculator
    {
        public Task RecalculateForPrincipalAsync(Guid principalId, IDocumentSession session,
            IReadOnlyCollection<string>? changedPaths = null, CancellationToken ct = default)
            => throw new InvalidOperationException("ShouldSync test must not call recalculator.");

        public Task RecalculateForGroupAsync(Group group, IDocumentSession session, CancellationToken ct = default)
            => throw new InvalidOperationException("ShouldSync test must not call recalculator.");

        public Task RemoveUserFromAllAutoGroupsAsync(Guid principalId, IDocumentSession session, CancellationToken ct = default)
            => throw new InvalidOperationException("ShouldSync test must not call recalculator.");
    }

    public class PrincipalPathsConstants
    {
        // Each constant is the dotted path the membership-script dependency
        // collector emits for that field. The prefix MUST match
        // typeof(TPrincipal).Name + "." or scripts won't be invalidated when
        // the matching field changes.

        [Theory]
        [InlineData("Person.IsActive")]
        [InlineData("Person.IsDeleted")]
        [InlineData("Person.Email")]
        [InlineData("Person.NormalizedEmail")]
        [InlineData("Person.Firstname")]
        [InlineData("Person.Lastname")]
        [InlineData("Person.Acronym")]
        [InlineData("Person.AccountName")]
        public void Person_paths_match_dotted_prefix_format(string expected)
        {
            var values = new[]
            {
                PrincipalPaths.IsActive, PrincipalPaths.IsDeleted, PrincipalPaths.Email,
                PrincipalPaths.NormalizedEmail, PrincipalPaths.PersonFirstname,
                PrincipalPaths.PersonLastname, PrincipalPaths.PersonAcronym,
                PrincipalPaths.PersonUserName,
            };
            Assert.Contains(expected, values);
        }

        [Theory]
        [InlineData("Group.Email")]
        [InlineData("Group.Name")]
        [InlineData("Group.EmailMode")]
        public void Group_paths_match_dotted_prefix_format(string expected)
        {
            var values = new[]
            {
                PrincipalPaths.GroupEmail, PrincipalPaths.GroupName, PrincipalPaths.GroupEmailMode,
            };
            Assert.Contains(expected, values);
        }

        [Fact]
        public void GroupPrincipalPaths_All_aggregates_every_group_path_for_recalc()
        {
            // The "All" array is what GroupUpdated/GroupCreated handlers pass to
            // the recalculator — if a path is added to PrincipalPaths but not to
            // GroupPrincipalPaths.All, scripts watching that field won't fire.
            Assert.Contains(PrincipalPaths.GroupEmail, GroupPrincipalPaths.All);
            Assert.Contains(PrincipalPaths.GroupName, GroupPrincipalPaths.All);
            Assert.Contains(PrincipalPaths.GroupEmailMode, GroupPrincipalPaths.All);
        }
    }

    public class UserUpdatedShouldSync
    {
        // Test subclass exposes the protected ShouldSync so we can assert the
        // early-out without touching SyncAsync (which would need Marten).
        private sealed class TestableHandler : AutoMembershipOnUserUpdatedHandler
        {
            public TestableHandler() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnUserUpdatedHandler>.Instance) { }
            public new bool ShouldSync(UserUpdatedEvent @event) => base.ShouldSync(@event);
        }

        private static UserUpdatedEvent Empty(Guid id) =>
            new(id,
                Firstname: default(Optional<string>),
                Lastname: default(Optional<string>),
                Acronym: default(Optional<string>),
                Email: default(Optional<string>));

        [Fact]
        public void Skips_when_no_relevant_field_was_changed()
        {
            // All optionals "None" means the update touched only fields the
            // membership scripts can't depend on (e.g. password) — recalc would
            // be wasted work.
            var handler = new TestableHandler();
            Assert.False(handler.ShouldSync(Empty(Guid.NewGuid())));
        }

        [Theory]
        [InlineData("first")]
        [InlineData("last")]
        [InlineData("acronym")]
        [InlineData("email")]
        public void Triggers_when_any_indexed_field_changed(string field)
        {
            var id = Guid.NewGuid();
            var ev = field switch
            {
                "first" => new UserUpdatedEvent(id, Firstname: "X",
                    Lastname: default(Optional<string>),
                    Acronym: default(Optional<string>),
                    Email: default(Optional<string>)),
                "last" => new UserUpdatedEvent(id,
                    Firstname: default(Optional<string>),
                    Lastname: "Y",
                    Acronym: default(Optional<string>),
                    Email: default(Optional<string>)),
                "acronym" => new UserUpdatedEvent(id,
                    Firstname: default(Optional<string>),
                    Lastname: default(Optional<string>),
                    Acronym: "Z",
                    Email: default(Optional<string>)),
                _ => new UserUpdatedEvent(id,
                    Firstname: default(Optional<string>),
                    Lastname: default(Optional<string>),
                    Acronym: default(Optional<string>),
                    Email: "a@b"),
            };

            Assert.True(new TestableHandler().ShouldSync(ev));
        }
    }

    public class UnconditionalShouldSyncHandlers
    {
        // The remaining handlers (Activated, Deactivated, Deleted, Group*) always
        // sync because every field they react to may invalidate scripts. This
        // class tests their ShouldSync via subclasses to guard against someone
        // accidentally adding an early-out that breaks the script invalidation.

        private sealed class TestableActivated : AutoMembershipOnUserActivatedHandler
        {
            public TestableActivated() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnUserActivatedHandler>.Instance) { }
            public new bool ShouldSync(UserActivatedEvent @event) => base.ShouldSync(@event);
        }

        private sealed class TestableDeactivated : AutoMembershipOnUserDeactivatedHandler
        {
            public TestableDeactivated() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnUserDeactivatedHandler>.Instance) { }
            public new bool ShouldSync(UserDeactivatedEvent @event) => base.ShouldSync(@event);
        }

        private sealed class TestableDeleted : AutoMembershipOnUserDeletedHandler
        {
            public TestableDeleted() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnUserDeletedHandler>.Instance) { }
            public new bool ShouldSync(UserDeletedEvent @event) => base.ShouldSync(@event);
        }

        private sealed class TestableGroupUpdated : AutoMembershipOnGroupUpdatedHandler
        {
            public TestableGroupUpdated() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnGroupUpdatedHandler>.Instance) { }
            public new bool ShouldSync(GroupUpdatedEvent @event) => base.ShouldSync(@event);
        }

        private sealed class TestableGroupCreated : AutoMembershipOnGroupCreatedHandler
        {
            public TestableGroupCreated() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnGroupCreatedHandler>.Instance) { }
            public new bool ShouldSync(GroupCreatedEvent @event) => base.ShouldSync(@event);
        }

        private sealed class TestableGroupDeleted : AutoMembershipOnGroupDeletedHandler
        {
            public TestableGroupDeleted() : base(new ThrowingRecalculator(), NullLogger<AutoMembershipOnGroupDeletedHandler>.Instance) { }
            public new bool ShouldSync(GroupDeletedEvent @event) => base.ShouldSync(@event);
        }

        [Fact]
        public void UserActivated_always_syncs() =>
            Assert.True(new TestableActivated().ShouldSync(new UserActivatedEvent(Guid.NewGuid())));

        [Fact]
        public void UserDeactivated_always_syncs() =>
            Assert.True(new TestableDeactivated().ShouldSync(new UserDeactivatedEvent(Guid.NewGuid())));

        [Fact]
        public void UserDeleted_always_syncs() =>
            Assert.True(new TestableDeleted().ShouldSync(new UserDeletedEvent(Guid.NewGuid())));

        [Fact]
        public void GroupUpdated_always_syncs()
        {
            var ev = new GroupUpdatedEvent(Guid.NewGuid(), "G", null,
                new List<Guid>(), new List<Guid>(), new List<ResourceAccessScript>());
            Assert.True(new TestableGroupUpdated().ShouldSync(ev));
        }

        [Fact]
        public void GroupCreated_always_syncs()
        {
            var ev = new GroupCreatedEvent(Guid.NewGuid(), "G", null,
                new List<Guid>(), new List<Guid>(), new List<ResourceAccessScript>());
            Assert.True(new TestableGroupCreated().ShouldSync(ev));
        }

        [Fact]
        public void GroupDeleted_always_syncs() =>
            Assert.True(new TestableGroupDeleted().ShouldSync(new GroupDeletedEvent(Guid.NewGuid())));
    }
}
