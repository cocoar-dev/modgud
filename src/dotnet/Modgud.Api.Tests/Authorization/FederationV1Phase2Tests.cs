using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Federation v1 — Phase 2 (login-time membership deriver + two-engine parity).
/// Pins: the deriver evaluates ONLY Auto+ExternallyDrivable groups in-memory over
/// an EvalPrincipal (local Person ∪ provider groups), never writes MemberIds,
/// defensively drops realm:admin-conferring groups; and the in-memory engine
/// agrees with the persisted JSONB-batch engine on the shared local fields
/// (the reconciliation guardrail).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FederationV1Phase2Tests : IntegrationTestBase
{
    public FederationV1Phase2Tests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string GroupScript =
        "(p) => Type.Is(p, 'person') && p.IsActive && p.ExternalGroups.includes('entra-admins')";

    [Fact]
    public async Task Deriver_Matches_ExternallyDrivable_Group_Via_ExternalGroups()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Fed", "Driver", "FD", "fed-driver@acme.com");

        var drivable = await CreateAutoGroupAsync(GroupScript, externallyDrivable: true);
        var notDrivable = await CreateAutoGroupAsync(GroupScript, externallyDrivable: false);

        using var scope = Factory.Services.CreateScope();
        var deriver = scope.ServiceProvider.GetRequiredService<ILoginTimeMembershipDeriver>();

        var matched = await deriver.DeriveAsync(user.Id, ["entra-admins", "all-staff"], "provider:test", ct);

        Assert.Contains(drivable, matched.MatchedGroupIds);
        Assert.DoesNotContain(notDrivable, matched.MatchedGroupIds); // batch-only group never derived

        // No upstream group → no match.
        var none = await deriver.DeriveAsync(user.Id, ["all-staff"], "provider:test", ct);
        Assert.DoesNotContain(drivable, none.MatchedGroupIds);
    }

    [Fact]
    public async Task Deriver_Drops_RealmAdmin_Conferring_Group()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Fed", "Admin", "FA", "fed-admin@acme.com");
        var adminRole = await Factory.CreateTestRoleAsync($"RealmAdmin_{Guid.NewGuid():N}", isRealmAdmin: true);

        // Bypass the write-time config guard by emitting the event directly — this
        // is exactly the slipped-through case the deriver's defensive strip covers.
        var alwaysMatch = "(p) => Type.Is(p, 'person')";
        var rogue = await CreateAutoGroupAsync(alwaysMatch, externallyDrivable: true, roleIds: [adminRole.Id]);

        using var scope = Factory.Services.CreateScope();
        var deriver = scope.ServiceProvider.GetRequiredService<ILoginTimeMembershipDeriver>();

        var matched = await deriver.DeriveAsync(user.Id, [], "provider:test", ct);
        Assert.DoesNotContain(rogue, matched.MatchedGroupIds);
    }

    [Fact]
    public async Task Deriver_Never_Writes_MemberIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Fed", "Ephemeral", "FE", "fed-eph@acme.com");
        var drivable = await CreateAutoGroupAsync(GroupScript, externallyDrivable: true);

        using var scope = Factory.Services.CreateScope();
        var deriver = scope.ServiceProvider.GetRequiredService<ILoginTimeMembershipDeriver>();
        var matched = await deriver.DeriveAsync(user.Id, ["entra-admins"], "provider:test", ct);
        Assert.Contains(drivable, matched.MatchedGroupIds);

        // The match is session-only — durable MemberIds must stay empty.
        await using var read = GetTenantedSession();
        var group = await read.LoadAsync<Group>(drivable, ct);
        Assert.NotNull(group);
        Assert.DoesNotContain(user.Id, group!.MemberIds);
    }

    [Fact]
    public async Task Reconciliation_InMemory_Agrees_With_JsonbBatch_On_Local_Fields()
    {
        var ct = TestContext.Current.CancellationToken;

        // Edge cases: domain match, mixed-case, non-match.
        var alice = await Factory.CreateTestUserWithIdentityAsync("Alice", "A", "AA", "alice@acme.com");
        var bob = await Factory.CreateTestUserWithIdentityAsync("Bob", "B", "BB", "bob@ACME.com");
        var carol = await Factory.CreateTestUserWithIdentityAsync("Carol", "C", "CC", "carol@contoso.com");
        var users = new[] { alice.Id, bob.Id, carol.Id };

        const string localScript =
            "(p) => Type.Is(p, 'person') && p.Email != null && p.Email.endsWith('@acme.com')";

        // ── JSONB batch engine: a non-drivable Auto group, materialized via SQL.
        var batchGroupId = await CreateAutoGroupAsync(localScript, externallyDrivable: false);
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
            var group = await session.LoadAsync<Group>(batchGroupId, ct);
            await recalc.RecalculateForGroupAsync(group!, session, ct);
            await session.SaveChangesAsync(ct);
        }

        HashSet<Guid> batchMembers;
        await using (var read = GetTenantedSession())
        {
            var group = await read.LoadAsync<Group>(batchGroupId, ct);
            batchMembers = group!.MemberIds.ToHashSet();
        }

        // ── In-memory engine: evaluate the SAME script over an EvalPrincipal
        //    hydrated from each persisted Person.
        using var scope2 = Factory.Services.CreateScope();
        var query = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var evaluator = scope2.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var compiled = evaluator.TranspileMembershipScript(localScript);
        var predicate = evaluator.BuildPredicate<EvalPrincipal>(compiled, ct).Compile();

        foreach (var id in users)
        {
            var person = await query.LoadAsync<Person>(id, ct);
            var eval = new EvalPrincipal
            {
                Id = person!.Id,
                IsActive = person.IsActive,
                IsDeleted = person.IsDeleted,
                AccountName = person.AccountName,
                Firstname = person.Firstname,
                Lastname = person.Lastname,
                Acronym = person.Acronym,
                Email = person.Email,
                NormalizedUserName = person.NormalizedUserName,
                NormalizedEmail = person.NormalizedEmail,
                ExternalIdentities = person.ExternalIdentities,
            };
            var inMemory = predicate(eval);
            var inBatch = batchMembers.Contains(id);
            Assert.True(inMemory == inBatch,
                $"Engine divergence for {person.Email}: in-memory={inMemory}, batch={inBatch}");
        }
    }

    private async Task<Guid> CreateAutoGroupAsync(
        string script, bool externallyDrivable, List<Guid>? roleIds = null)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var compiled = evaluator.TranspileMembershipScript(script);

        var id = Guid.CreateVersion7();
        session.Events.StartStream(id, new GroupCreatedEvent(
            id, $"Fed_{Guid.NewGuid():N}", null,
            [], roleIds ?? [],
            MembershipMode.Auto, script, compiled, null,
            null, EmailMode.Shared,
            [AppSlugs.Modgud],
            externallyDrivable));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
