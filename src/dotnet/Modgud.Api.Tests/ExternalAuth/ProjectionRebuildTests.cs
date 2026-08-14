using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Authentication.Identity.ExternalAuth;
using Modgud.Authentication.Projections;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// Proves the lifecycle changes survive a full projection rebuild ("replay").
/// Two load-bearing claims from the Phase-3/4 design:
/// <list type="bullet">
///   <item>Variant-C unlink (terminal event → <c>ShouldDelete</c>) replays to
///         "no doc" — an unlinked link is NOT resurrected, and a re-link of the
///         same <c>(Issuer, Subject)</c> does not trigger a unique-index
///         violation mid-rebuild (Marten applies the stream's
///         <c>Linked → Unlinked → Linked</c> in sequence order).</item>
///   <item>Auto-group membership is reproduced verbatim from the frozen,
///         self-contained <c>GroupMembershipRecomputedEvent</c> — rebuild does
///         NOT re-evaluate scripts, so the MemberIds set is identical before and
///         after.</item>
///   <item>GDPR-archived streams are skipped on rebuild — an erased user's
///         Person + link docs stay gone (erased PII does not come back).</item>
/// </list>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ProjectionRebuildTests : IntegrationTestBase
{
    public ProjectionRebuildTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Rebuild_Preserves_Membership_And_Does_Not_Resurrect_Unlinked_Links()
    {
        var ct = TestContext.Current.CancellationToken;
        const string issuer = "https://idp.rebuild-a.test/v2.0";
        var groupId = await CreateAutoGroupAsync(
            $"(p) => Type.Is(p, 'person') && p.ExternalIdentities.some(x => x.Issuer === '{issuer}')");

        var userKeep = await Factory.CreateTestUserWithIdentityAsync("Keep", "Member", "km", "keep@acme.com");
        var userRelink = await Factory.CreateTestUserWithIdentityAsync("Re", "Link", "rl", "relink@acme.com");

        await SeedLinkAsync(userKeep.Id, Guid.NewGuid(), issuer, "sub-keep");

        // link → unlink (ShouldDelete drops the doc) → re-link the SAME (iss,sub).
        // The old + new streams both carry Subject="sub-relink": the strongest
        // rebuild stress for the unique index.
        var oldLinkId = await SeedLinkAsync(userRelink.Id, Guid.NewGuid(), issuer, "sub-relink");
        await UnlinkViaEventsAsync(userRelink.Id, oldLinkId, Guid.NewGuid());
        var newLinkId = await SeedLinkAsync(userRelink.Id, Guid.NewGuid(), issuer, "sub-relink");

        await RecalcGroupAsync(groupId);

        var before = (await MemberIdsAsync(groupId)).OrderBy(x => x).ToArray();
        Assert.Contains(userKeep.Id, before);
        Assert.Contains(userRelink.Id, before);

        await RebuildAsync(ct);

        // Membership reproduced verbatim from the frozen recompute event.
        var after = (await MemberIdsAsync(groupId)).OrderBy(x => x).ToArray();
        Assert.Equal(before, after);

        // The unlinked link did NOT resurrect; exactly one live link per (iss,sub).
        await using var qs = GetTenantedSession();
        Assert.Null(await qs.LoadAsync<ExternalIdentityLink>(oldLinkId, ct));
        Assert.NotNull(await qs.LoadAsync<ExternalIdentityLink>(newLinkId, ct));
        Assert.Equal(1, await qs.Query<ExternalIdentityLink>().Where(l => l.Subject == "sub-relink").CountAsync(ct));
        Assert.Equal(1, await qs.Query<ExternalIdentityLink>().Where(l => l.Subject == "sub-keep").CountAsync(ct));
    }

    [Fact]
    public async Task Rebuild_Skips_GdprArchived_Streams_And_Does_Not_Resurrect_Erased_Data()
    {
        var ct = TestContext.Current.CancellationToken;
        const string issuer = "https://idp.rebuild-b.test/v2.0";
        var user = await Factory.CreateTestUserWithIdentityAsync("Erase", "Me", "em", "eraseme@acme.com");
        var linkId = await SeedLinkAsync(user.Id, Guid.NewGuid(), issuer, "sub-erase");

        await using (var qs = GetTenantedSession())
        {
            Assert.NotNull(await qs.LoadAsync<Person>(user.Id, ct));
            Assert.NotNull(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
        }

        // GDPR permanent erase archives the user stream + the link stream.
        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var r = await gdpr.PermanentlyEraseAsync(user.Id, adminUserId: null, reason: "rebuild-test", ct);
            Assert.False(r.IsError, r.IsError ? r.FirstError.Description : null);
        }

        await RebuildAsync(ct);

        // Archived streams are skipped → neither doc comes back; erased data stays erased.
        await using (var qs = GetTenantedSession())
        {
            Assert.Null(await qs.LoadAsync<Person>(user.Id, ct));
            Assert.Null(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task RebuildAsync(CancellationToken ct)
    {
        await Factory.RebuildProjectionAsync<ExternalIdentityLinkProjection>(ct: ct);
        await Factory.RebuildProjectionAsync<PersonProjection>(ct: ct);
        await Factory.RebuildProjectionAsync<GroupProjection>(ct: ct);
    }

    private async Task<Guid> SeedLinkAsync(Guid userId, Guid providerId, string issuer, string subject)
    {
        var linkId = Guid.NewGuid();
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<ExternalIdentityLink>(linkId, new ExternalIdentityLinkedEvent(
            linkId, userId, providerId, issuer, subject, null, null, DateTimeOffset.UtcNow));
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            userId, linkId, providerId, issuer, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return linkId;
    }

    private async Task UnlinkViaEventsAsync(Guid userId, Guid linkId, Guid providerId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(linkId, new ExternalIdentityUnlinkedEvent(linkId, DateTimeOffset.UtcNow, userId));
        session.Events.Append(userId, new UserExternalIdentityUnlinkedEvent(userId, linkId, providerId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateAutoGroupAsync(string script)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var compiled = evaluator.TranspileMembershipScript(script);
        var id = Guid.CreateVersion7();
        session.Events.StartStream(id, new GroupCreatedEvent(
            id, $"Reb_{Guid.NewGuid():N}"[..12], null, [], [],
            MembershipMode.Auto, script, compiled, null, null, EmailMode.Shared,
            [AppSlugs.Modgud], ExternallyDrivable: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task RecalcGroupAsync(Guid groupId)
    {
        using var scope = Factory.Services.CreateScope();
        var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var group = await session.LoadAsync<Group>(groupId, TestContext.Current.CancellationToken);
        await recalc.RecalculateForGroupAsync(group!, session, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> MemberIdsAsync(Guid groupId)
    {
        await using var qs = GetTenantedSession();
        var group = await qs.LoadAsync<Group>(groupId, TestContext.Current.CancellationToken);
        return group!.MemberIds;
    }
}
