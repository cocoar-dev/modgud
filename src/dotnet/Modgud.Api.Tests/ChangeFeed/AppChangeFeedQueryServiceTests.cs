using Marten;
using Modgud.Api.Features.ChangeFeed;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Infrastructure.ChangeFeed;

namespace Modgud.Api.Tests.ChangeFeed;

[Collection(IntegrationTestCollection.Name)]
public class AppChangeFeedQueryServiceTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Snapshot_and_incremental_read_share_one_opaque_checkpoint_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.CreateVersion7();
        var principalId = Guid.CreateVersion7();
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Events.StartStream<App>(appId, new AppCreatedEvent(
                appId, "feed-query-test", "Feed query test", null, [], false));
            arrange.Store(new AppChangeFeedState
            {
                Id = appId,
                Enabled = true,
                Generation = 1,
                ScopeVersion = "v1-test",
                LastProcessedSequence = 20,
            });
            arrange.Store(new AppChangeFeedEntityState
            {
                Id = Guid.CreateVersion7(),
                AppId = appId,
                EntityKind = "principal",
                EntityId = principalId,
                Fingerprint = "test",
                PayloadJson = "{\"DisplayName\":\"Current\"}",
            });
            arrange.Store(new AppChangeFeedEntry
            {
                Id = Guid.CreateVersion7(),
                AppId = appId,
                Generation = 1,
                SourceSequence = 11,
                Ordinal = 0,
                ScopeVersion = "v1-test",
                RecordedAt = DateTimeOffset.UtcNow,
                OriginatedAt = DateTimeOffset.UtcNow,
                ChangeKind = "Upsert",
                EntityKind = "principal",
                EntityId = principalId,
                PayloadJson = "{\"DisplayName\":\"Current\"}",
            });
            await arrange.SaveChangesAsync(ct);
        }

        await using var query = GetTenantedSession();
        var service = new AppChangeFeedQueryService(query);
        var snapshotResult = await service.SnapshotAsync(appId, ct);

        Assert.True(snapshotResult.IsSuccess);
        var snapshot = snapshotResult.Value!;
        Assert.Equal(1, snapshot.ContractVersion);
        Assert.Equal("feed-query-test", snapshot.AppSlug);
        Assert.Equal("v1-test", snapshot.ScopeVersion);
        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal("principal", entity.EntityKind);
        Assert.Equal("Current", entity.Payload.GetProperty("DisplayName").GetString());
        Assert.True(AppChangeFeedCursor.TryDecode(snapshot.Cursor, out var checkpoint));
        Assert.Equal(20, checkpoint.Sequence);
        Assert.Equal(int.MaxValue, checkpoint.Ordinal);

        var readResult = await service.ReadAsync(
            appId,
            AppChangeFeedCursor.Encode(appId, 1, 10, int.MaxValue),
            limit: 100,
            ct);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(1, readResult.Value!.ContractVersion);
        Assert.Equal("v1-test", readResult.Value.ScopeVersion);
        Assert.Collection(
            readResult.Value.Messages,
            change =>
            {
                Assert.Equal("Change", change.Kind);
                Assert.Equal("Upsert", change.ChangeKind);
                Assert.Equal("principal", change.EntityKind);
            },
            finalCheckpoint =>
            {
                Assert.Equal("Checkpoint", finalCheckpoint.Kind);
                Assert.Equal(snapshot.Cursor, finalCheckpoint.Cursor);
            });
        Assert.False(readResult.Value.HasMore);
    }

    [Fact]
    public async Task Read_explicitly_distinguishes_scope_reset_from_expired_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.CreateVersion7();
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(new AppChangeFeedState
            {
                Id = appId,
                Enabled = true,
                Generation = 4,
                ScopeVersion = "v4-test",
                LastProcessedSequence = 100,
                RetentionFloorSequence = 50,
                RetentionFloorOrdinal = 2,
            });
            await arrange.SaveChangesAsync(ct);
        }

        await using var query = GetTenantedSession();
        var service = new AppChangeFeedQueryService(query);

        var wrongGeneration = await service.ReadAsync(
            appId, AppChangeFeedCursor.Encode(appId, 3, 80, 0), 100, ct);
        Assert.False(wrongGeneration.IsSuccess);
        Assert.Equal("ScopeChanged", wrongGeneration.Error!.Code);

        var expired = await service.ReadAsync(
            appId, AppChangeFeedCursor.Encode(appId, 4, 50, 2), 100, ct);
        Assert.False(expired.IsSuccess);
        Assert.Equal("CursorTooOld", expired.Error!.Code);
    }
}
