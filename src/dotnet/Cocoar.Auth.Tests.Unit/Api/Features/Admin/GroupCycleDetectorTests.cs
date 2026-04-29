using Cocoar.Auth.Api.Features.Admin;
using Cocoar.Auth.Authorization.Principals;

namespace Cocoar.Auth.Tests.Unit.Api.Features.Admin;

/// <summary>
/// Pinning tests for <see cref="GroupCycleDetector"/>. <c>UpdateGroupCommand</c>
/// already prevents creation of cycles in the happy path — this defensive
/// scanner is what catches historical data or out-of-band DB edits, so its
/// dedup + path-extraction behaviour matters for the admin consistency-check.
/// </summary>
public class GroupCycleDetectorTests
{
    private static Group MakeGroup(string name, params Guid[] memberIds)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            MemberIds = memberIds.ToList(),
        };

    public class DetectCycles
    {
        [Fact]
        public void Returns_empty_for_an_empty_graph()
        {
            Assert.Empty(GroupCycleDetector.DetectCycles(new List<Group>()));
        }

        [Fact]
        public void Returns_empty_for_an_acyclic_graph()
        {
            // A → B → C, no back-edge.
            var c = MakeGroup("C");
            var b = MakeGroup("B", c.Id);
            var a = MakeGroup("A", b.Id);

            Assert.Empty(GroupCycleDetector.DetectCycles(new() { a, b, c }));
        }

        [Fact]
        public void Detects_a_simple_two_node_cycle()
        {
            // A → B → A
            var aId = Guid.NewGuid();
            var bId = Guid.NewGuid();
            var a = new Group { Id = aId, Name = "A", MemberIds = [bId] };
            var b = new Group { Id = bId, Name = "B", MemberIds = [aId] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a, b });

            Assert.Single(cycles);
            var ids = cycles[0].Groups.Select(g => g.Id).OrderBy(id => id).ToArray();
            Assert.Equal(new[] { aId, bId }.OrderBy(id => id).ToArray(), ids);
        }

        [Fact]
        public void Deduplicates_the_same_cycle_visited_from_different_starts()
        {
            // Same A↔B cycle reached from A first and from B second must report once,
            // otherwise the admin gets the same alert twice per cycle.
            var aId = Guid.NewGuid();
            var bId = Guid.NewGuid();
            var a = new Group { Id = aId, Name = "A", MemberIds = [bId] };
            var b = new Group { Id = bId, Name = "B", MemberIds = [aId] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a, b });
            Assert.Single(cycles);
        }

        [Fact]
        public void Detects_a_self_loop()
        {
            // A group whose MemberIds contains its own id (A → A).
            var id = Guid.NewGuid();
            var a = new Group { Id = id, Name = "A", MemberIds = [id] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a });

            Assert.Single(cycles);
            Assert.Equal(new[] { id }, cycles[0].Groups.Select(g => g.Id).ToArray());
        }

        [Fact]
        public void Detects_a_three_node_cycle()
        {
            // A → B → C → A
            var aId = Guid.NewGuid();
            var bId = Guid.NewGuid();
            var cId = Guid.NewGuid();
            var a = new Group { Id = aId, Name = "A", MemberIds = [bId] };
            var b = new Group { Id = bId, Name = "B", MemberIds = [cId] };
            var c = new Group { Id = cId, Name = "C", MemberIds = [aId] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a, b, c });

            Assert.Single(cycles);
            Assert.Equal(3, cycles[0].Groups.Count);
        }

        [Fact]
        public void Reports_two_disjoint_cycles_separately()
        {
            // A↔B and C↔D — two independent cycles in the same graph.
            var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
            var cId = Guid.NewGuid(); var dId = Guid.NewGuid();
            var a = new Group { Id = aId, Name = "A", MemberIds = [bId] };
            var b = new Group { Id = bId, Name = "B", MemberIds = [aId] };
            var c = new Group { Id = cId, Name = "C", MemberIds = [dId] };
            var d = new Group { Id = dId, Name = "D", MemberIds = [cId] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a, b, c, d });
            Assert.Equal(2, cycles.Count);
        }

        [Fact]
        public void Ignores_member_ids_that_are_not_groups()
        {
            // Person ids referenced in MemberIds must not be walked into — they
            // can never cycle back through the group-membership graph.
            var personId = Guid.NewGuid();
            var a = MakeGroup("A", personId);

            Assert.Empty(GroupCycleDetector.DetectCycles(new() { a }));
        }

        [Fact]
        public void Reports_group_names_alongside_ids_for_admin_ui_rendering()
        {
            var aId = Guid.NewGuid();
            var bId = Guid.NewGuid();
            var a = new Group { Id = aId, Name = "Engineers", MemberIds = [bId] };
            var b = new Group { Id = bId, Name = "Leads", MemberIds = [aId] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a, b });

            Assert.Single(cycles);
            var names = cycles[0].Groups.Select(g => g.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Engineers", "Leads" }, names);
        }

        [Fact]
        public void Does_not_report_cycles_for_a_group_that_only_appears_in_a_path_to_a_cycle()
        {
            // Linear chain that LEADS into a cycle: A → B → C → D → C.
            // Only the C↔D back-edge is the cycle; A and B should not appear.
            var aId = Guid.NewGuid();
            var bId = Guid.NewGuid();
            var cId = Guid.NewGuid();
            var dId = Guid.NewGuid();
            var a = new Group { Id = aId, Name = "A", MemberIds = [bId] };
            var b = new Group { Id = bId, Name = "B", MemberIds = [cId] };
            var c = new Group { Id = cId, Name = "C", MemberIds = [dId] };
            var d = new Group { Id = dId, Name = "D", MemberIds = [cId] };

            var cycles = GroupCycleDetector.DetectCycles(new() { a, b, c, d });

            Assert.Single(cycles);
            var idsInCycle = cycles[0].Groups.Select(g => g.Id).ToHashSet();
            Assert.Contains(cId, idsInCycle);
            Assert.Contains(dId, idsInCycle);
            Assert.DoesNotContain(aId, idsInCycle);
            Assert.DoesNotContain(bId, idsInCycle);
        }
    }
}
