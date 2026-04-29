using Cocoar.Auth.Authorization.Principals;

namespace Cocoar.Auth.Tests.Unit.Authorization.Principals;

/// <summary>
/// Pins the email-resolution behaviour of <see cref="Group"/>. The class is
/// otherwise a DTO, but <c>GetEmailsAsync</c> implements meaningful policy:
/// <list type="bullet">
///   <item>Shared address wins when set</item>
///   <item>Shared falls back to ExpandToMembers when the address is blank</item>
///   <item>ExpandToMembers recurses through nested groups</item>
///   <item>Inactive / soft-deleted members are filtered out</item>
///   <item>Cycles in the group graph terminate (visited-set short-circuit)</item>
/// </list>
/// </summary>
public class GroupTests
{
    public class Type
    {
        [Fact]
        public void Is_stable_discriminator_string()
        {
            Assert.Equal("group", new Group().Type);
        }
    }

    public class DisplayName
    {
        [Fact]
        public void Returns_name_verbatim()
        {
            Assert.Equal("Admins", new Group { Name = "Admins" }.DisplayName);
        }
    }

    public class GetEmailsAsync_SharedMode
    {
        [Fact]
        public async Task Returns_shared_address_when_set()
        {
            var g = new Group { EmailMode = EmailMode.Shared, Email = "team@example.com" };
            var emails = await g.GetEmailsAsync(new MapEmailContext(), TestContext.Current.CancellationToken);
            Assert.Equal(["team@example.com"], emails);
        }

        [Fact]
        public async Task Falls_back_to_member_expansion_when_email_null()
        {
            // Documented fallback: misconfigured shared mailbox MUST NOT silently
            // drop notifications — the engine expands to members instead.
            var alice = new Person { Id = Guid.NewGuid(), Email = "alice@example.com" };
            var g = new Group
            {
                EmailMode = EmailMode.Shared,
                Email = null,
                MemberIds = [alice.Id],
            };
            var ctx = new MapEmailContext { [alice.Id] = alice };
            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["alice@example.com"], emails);
        }

        [Fact]
        public async Task Falls_back_to_member_expansion_when_email_whitespace()
        {
            var alice = new Person { Id = Guid.NewGuid(), Email = "alice@example.com" };
            var g = new Group
            {
                EmailMode = EmailMode.Shared,
                Email = "   ",
                MemberIds = [alice.Id],
            };
            var ctx = new MapEmailContext { [alice.Id] = alice };
            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["alice@example.com"], emails);
        }
    }

    public class GetEmailsAsync_ExpandToMembers
    {
        [Fact]
        public async Task Collects_emails_from_addressable_members()
        {
            var alice = new Person { Id = Guid.NewGuid(), Email = "alice@example.com" };
            var bob = new Person { Id = Guid.NewGuid(), Email = "bob@example.com" };
            var g = new Group
            {
                Id = Guid.NewGuid(),
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [alice.Id, bob.Id],
            };
            var ctx = new MapEmailContext { [alice.Id] = alice, [bob.Id] = bob };

            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["alice@example.com", "bob@example.com"], emails);
        }

        [Fact]
        public async Task Recurses_into_nested_groups()
        {
            var alice = new Person { Id = Guid.NewGuid(), Email = "alice@example.com" };
            var inner = new Group
            {
                Id = Guid.NewGuid(),
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [alice.Id],
            };
            var outer = new Group
            {
                Id = Guid.NewGuid(),
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [inner.Id],
            };
            var ctx = new MapEmailContext { [alice.Id] = alice, [inner.Id] = inner };

            var emails = await outer.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["alice@example.com"], emails);
        }

        [Fact]
        public async Task Skips_inactive_members()
        {
            var alice = new Person { Id = Guid.NewGuid(), Email = "alice@example.com", IsActive = false };
            var bob = new Person { Id = Guid.NewGuid(), Email = "bob@example.com", IsActive = true };
            var g = new Group
            {
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [alice.Id, bob.Id],
            };
            var ctx = new MapEmailContext { [alice.Id] = alice, [bob.Id] = bob };

            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["bob@example.com"], emails);
        }

        [Fact]
        public async Task Skips_soft_deleted_members()
        {
            var alice = new Person { Id = Guid.NewGuid(), Email = "alice@example.com", IsDeleted = true };
            var bob = new Person { Id = Guid.NewGuid(), Email = "bob@example.com" };
            var g = new Group
            {
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [alice.Id, bob.Id],
            };
            var ctx = new MapEmailContext { [alice.Id] = alice, [bob.Id] = bob };

            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["bob@example.com"], emails);
        }

        [Fact]
        public async Task Skips_unresolvable_member_ids_silently()
        {
            // A dangling member id (member document gone but reference still on
            // group) must not crash the resolver — common during projection rebuilds.
            var bob = new Person { Id = Guid.NewGuid(), Email = "bob@example.com" };
            var g = new Group
            {
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [Guid.NewGuid(), bob.Id],
            };
            var ctx = new MapEmailContext { [bob.Id] = bob };

            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["bob@example.com"], emails);
        }

        [Fact]
        public async Task Skips_non_email_addressable_members()
        {
            var sa = new ServiceAccount { Id = Guid.NewGuid(), AccountName = "build-bot" };
            var bob = new Person { Id = Guid.NewGuid(), Email = "bob@example.com" };
            var g = new Group
            {
                EmailMode = EmailMode.ExpandToMembers,
                MemberIds = [sa.Id, bob.Id],
            };
            var ctx = new MapEmailContext { [sa.Id] = sa, [bob.Id] = bob };

            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Equal(["bob@example.com"], emails);
        }

        [Fact]
        public async Task Cycle_through_self_membership_terminates()
        {
            // Self-loop: groups with their own id in MemberIds must not recurse forever.
            // The visited-set seeded with the group's own Id breaks the cycle.
            var g = new Group
            {
                Id = Guid.NewGuid(),
                EmailMode = EmailMode.ExpandToMembers,
            };
            g.MemberIds.Add(g.Id);
            var ctx = new MapEmailContext { [g.Id] = g };

            var emails = await g.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Empty(emails);
        }

        [Fact]
        public async Task Cycle_between_two_groups_terminates()
        {
            var a = new Group { Id = Guid.NewGuid(), EmailMode = EmailMode.ExpandToMembers };
            var b = new Group { Id = Guid.NewGuid(), EmailMode = EmailMode.ExpandToMembers };
            a.MemberIds.Add(b.Id);
            b.MemberIds.Add(a.Id);
            var ctx = new MapEmailContext { [a.Id] = a, [b.Id] = b };

            var emails = await a.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            Assert.Empty(emails);
        }

        [Fact]
        public async Task Returns_empty_for_group_with_no_members()
        {
            var g = new Group { Id = Guid.NewGuid(), EmailMode = EmailMode.ExpandToMembers };
            var emails = await g.GetEmailsAsync(new MapEmailContext(), TestContext.Current.CancellationToken);
            Assert.Empty(emails);
        }
    }

    public class CapabilityInterfaces
    {
        [Fact]
        public void Implements_members_and_email_addressable()
        {
            var g = new Group();
            Assert.IsAssignableFrom<IPrincipalWithMembers>(g);
            Assert.IsAssignableFrom<IPrincipalEmailAddressable>(g);
        }

        [Fact]
        public void Member_ids_are_exposed_through_interface()
        {
            // The interface returns IReadOnlyList; the class keeps a mutable List
            // for projection use. The two MUST point at the same backing storage
            // so an admin's mutation is immediately visible through the interface.
            var g = new Group();
            g.MemberIds.Add(Guid.NewGuid());
            Assert.Equal(g.MemberIds.Count, ((IPrincipalWithMembers)g).MemberIds.Count);
        }
    }

    private sealed class MapEmailContext : Dictionary<Guid, IPrincipal>, IEmailResolutionContext
    {
        public Task<IPrincipal?> LoadPrincipalAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(TryGetValue(id, out var p) ? p : null);
    }
}
