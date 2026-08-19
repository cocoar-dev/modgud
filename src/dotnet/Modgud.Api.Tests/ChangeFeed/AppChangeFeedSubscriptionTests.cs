using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Api.Features.ChangeFeed;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.ChangeFeed;

namespace Modgud.Api.Tests.ChangeFeed;

[Collection(IntegrationTestCollection.Name)]
public class AppChangeFeedSubscriptionTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Enable_seeds_snapshot_then_emits_net_change_and_scope_reset()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var appId = Guid.CreateVersion7();
        var appSlug = $"feed-{suffix}";
        var serviceAccountId = Guid.CreateVersion7();
        var groupId = Guid.CreateVersion7();

        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Events.StartStream<App>(appId, new AppCreatedEvent(
                appId, appSlug, "Feed subscription test", null, [], false));
            arrange.Events.StartStream<ServiceAccount>(serviceAccountId,
                new ServiceAccountCreatedEvent(
                    serviceAccountId, $"feed-agent-{suffix}", "Before", true));
            arrange.Events.StartStream<Group>(groupId, new GroupCreatedEvent(
                groupId,
                $"Feed group {suffix}",
                null,
                [serviceAccountId],
                [],
                BoundTo: [appSlug]));
            await arrange.SaveChangesAsync(ct);
        }

        // SubscribeFromPresent anchors the short-lived queue without replaying
        // the permanent history. Establish that anchor before enablement.
        await Factory.WaitForProjectionsAsync();

        var settings = new ApplicationSettingsDto
        {
            ChangeFeed = new ApplicationChangeFeedDto
            {
                Enabled = true,
                MinimumRetentionAgeDays = 7,
                MinimumEventCount = 1_000,
            },
        };
        var enable = await Client.PutAsJsonAsync(
            $"/api/app/{ShortGuid.Encode(appId)}",
            new UpdateAppDto("Feed subscription test", null, [], settings),
            JsonOptions,
            ct);
        enable.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        await using (var query = GetTenantedSession())
        {
            var state = await query.LoadAsync<AppChangeFeedState>(appId, ct);
            Assert.NotNull(state);
            Assert.True(state!.Enabled);
            Assert.Equal(1, state.Generation);

            var projected = await query.Query<AppChangeFeedEntityState>()
                .Where(x => x.AppId == appId)
                .ToListAsync(ct);
            Assert.Contains(projected, x => x.EntityKind == "principal" && x.EntityId == groupId);
            Assert.Contains(projected, x => x.EntityKind == "principal" && x.EntityId == serviceAccountId);

            var snapshot = await new AppChangeFeedQueryService(query).SnapshotAsync(appId, ct);
            Assert.True(snapshot.IsSuccess);
            Assert.Contains(snapshot.Value!.Entities,
                x => x.EntityKind == "principal" && x.EntityId == ShortGuid.Encode(groupId));
            Assert.Contains(snapshot.Value.Entities,
                x => x.EntityKind == "principal" && x.EntityId == ShortGuid.Encode(serviceAccountId));
        }

        var update = await Client.PutAsJsonAsync(
            $"/api/service-account/{ShortGuid.Encode(serviceAccountId)}",
            new { Purpose = "After" },
            JsonOptions,
            ct);
        update.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        await using (var query = GetTenantedSession())
        {
            var upsert = await query.Query<AppChangeFeedEntry>()
                .Where(x => x.AppId == appId
                            && x.Generation == 1
                            && x.ChangeKind == "Upsert"
                            && x.EntityId == serviceAccountId)
                .SingleAsync(ct);
            Assert.Equal("After", JsonDocument.Parse(upsert.PayloadJson!)
                .RootElement.GetProperty("Purpose").GetString());
        }

        await using (var mutate = GetTenantedDocumentSession())
        {
            mutate.Events.Append(groupId, new GroupUpdatedEvent(
                groupId,
                $"Feed group {suffix}",
                null,
                [serviceAccountId],
                [],
                BoundTo: []));
            await mutate.SaveChangesAsync(ct);
        }
        await Factory.WaitForProjectionsAsync();

        await using (var query = GetTenantedSession())
        {
            var state = await query.LoadAsync<AppChangeFeedState>(appId, ct);
            Assert.Equal(2, state!.Generation);
            var remaining = await query.Query<AppChangeFeedEntityState>()
                .Where(x => x.AppId == appId)
                .ToListAsync(ct);
            Assert.DoesNotContain(remaining,
                x => x.EntityKind == "principal" && x.EntityId == groupId);
            Assert.DoesNotContain(remaining,
                x => x.EntityKind == "principal" && x.EntityId == serviceAccountId);
            Assert.True(await query.Query<AppChangeFeedEntry>()
                .AnyAsync(x => x.AppId == appId
                               && x.Generation == 2
                               && x.ChangeKind == "ScopeChanged", ct));
        }
    }
}
