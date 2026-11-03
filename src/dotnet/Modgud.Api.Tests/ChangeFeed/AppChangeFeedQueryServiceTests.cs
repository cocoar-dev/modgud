using Marten;
using Modgud.Api.Features.ChangeFeed;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Infrastructure.ChangeFeed;
using Modgud.Authentication.Sessions;

namespace Modgud.Api.Tests.ChangeFeed;

[Collection(IntegrationTestCollection.Name)]
public class AppChangeFeedQueryServiceTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Recovery_history_survives_queue_deletion_generation_change_and_projection_lag()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var appId = Guid.NewGuid();
        var sid = Guid.NewGuid();
        var boundary = Guid.NewGuid();
        var expired = Guid.NewGuid();
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Events.StartStream<App>(appId, new AppCreatedEvent(appId, "recovery-history", "History", null, [], false));
            var state = new AppChangeFeedState
            {
                Id = appId, Enabled = true, Generation = 1, ScopeVersion = "old", LastProcessedSequence = 1,
            };
            arrange.Store(state);
            arrange.Store(new AppChangeFeedEntry
            {
                Id = Guid.NewGuid(), AppId = appId, Generation = 1, SourceSequence = 2,
                EntityKind = "session", ChangeKind = "Deleted", EntityId = sid,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { SessionId = sid.ToString() }),
                RecordedAt = now, OriginatedAt = now,
            });
            foreach (var (sessionId, endedAt, application) in new[]
            {
                (sid, now.AddMinutes(-5), appId),
                (sid, now.AddMinutes(-1), appId), // overlapping writes retain maximum end
                (boundary, now.AddDays(-7), appId),
                (expired, now.AddDays(-7).AddMilliseconds(-1), appId),
                (Guid.NewGuid(), now, Guid.NewGuid()),
            })
                arrange.Store(new SessionRevocation { Id = Guid.NewGuid(), AppId = application, SessionId = sessionId, EndedAt = endedAt });
            await arrange.SaveChangesAsync(ct);
            Assert.Single(await arrange.Query<AppChangeFeedEntry>().Where(e => e.AppId == appId).ToListAsync(ct));
            // Persisted queue deletion and generation switch, not merely an
            // advanced resume floor or an initially empty queue.
            state.Generation = 2;
            state.ScopeVersion = "new";
            arrange.Store(state);
            arrange.DeleteWhere<AppChangeFeedEntry>(e => e.AppId == appId);
            await arrange.SaveChangesAsync(ct);
        }
        await using var read = GetTenantedSession();
        Assert.Empty(await read.Query<AppChangeFeedEntry>().Where(e => e.AppId == appId).ToListAsync(ct));
        var query = new AppChangeFeedQueryService(read, new FixedClock(now));
        var snapshot = await query.SessionRevocationsAsync(appId, SessionRevocation.RetentionSeconds, ct);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(2, snapshot.Value!.Revocations.Count);
        Assert.Contains(snapshot.Value.Revocations, r => r.SessionId == sid.ToString() && r.EndedAt == now.AddMinutes(-1));
        Assert.Contains(snapshot.Value.Revocations, r => r.SessionId == boundary.ToString());
        Assert.True(AppChangeFeedCursor.TryDecode(snapshot.Value.Cursor, out var cursor));
        Assert.Equal(2, cursor.Generation);
        Assert.Equal(1, cursor.Sequence); // history must not wait for async projection
        foreach (var unsupported in new[] { 0, -1, SessionRevocation.RetentionSeconds + 1 })
        {
            var invalid = await query.SessionRevocationsAsync(appId, unsupported, ct);
            Assert.False(invalid.IsSuccess);
            Assert.Equal("UnsupportedRevocationHistory", invalid.Error!.Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_attribution_and_grant_deletion_commit_together(bool userWide)
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.NewGuid();
        var sid = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var absentId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // No currently associated client or scoped principal: only the exact
        // published grant state is evidence. A removed state cannot be recovered.
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(new SessionGrant { Id = grantId, SessionId = sid, UserId = DefaultUser!.Id, ClientId = "unmapped" });
            arrange.Store(new SessionGrant { Id = absentId, SessionId = Guid.NewGuid(), UserId = DefaultUser.Id, ClientId = "unmapped" });
            arrange.Store(new AppChangeFeedEntityState { Id = Guid.NewGuid(), AppId = appId, EntityKind = "session", EntityId = grantId });
            arrange.Store(new AppChangeFeedEntityState { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), EntityKind = "principal", EntityId = grantId });
            await arrange.SaveChangesAsync(ct);
        }
        await using (var rollback = GetTenantedDocumentSession())
        {
            var service = new SessionGrantService(rollback, new FixedClock(now));
            await service.StageSessionEndAsync(rollback, DefaultUser!.Id, sid, "test", null, ct);
            // Deliberately no save: neither the end nor the grant removal survives.
        }
        await using (var check = GetTenantedSession())
        {
            Assert.NotNull(await check.LoadAsync<SessionGrant>(grantId, ct));
            Assert.Empty(await check.Query<SessionRevocation>().ToListAsync(ct));
        }
        await using (var commit = GetTenantedDocumentSession())
        {
            var service = new SessionGrantService(commit, new FixedClock(now));
            if (userWide) await service.StageUserEndAsync(commit, DefaultUser!.Id, "test", ct);
            else await service.StageSessionEndAsync(commit, DefaultUser!.Id, sid, "test", null, ct);
            await commit.SaveChangesAsync(ct);
        }
        await using var verify = GetTenantedSession();
        Assert.Null(await verify.LoadAsync<SessionGrant>(grantId, ct));
        var record = Assert.Single(await verify.Query<SessionRevocation>().ToListAsync(ct));
        Assert.Equal(appId, record.AppId);
        Assert.Equal(sid, record.SessionId);
        Assert.Equal(now, record.EndedAt);
    }

    [Fact]
    public async Task Retention_sweep_keeps_exact_boundary_without_any_grants()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var keepId = Guid.NewGuid();
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(new SessionRevocation { Id = keepId, EndedAt = now.AddDays(-7) });
            arrange.Store(new SessionRevocation { Id = Guid.NewGuid(), EndedAt = now.AddDays(-7).AddMilliseconds(-1) });
            await arrange.SaveChangesAsync(ct);
        }
        await using (var sweep = GetTenantedDocumentSession())
            Assert.Equal(0, await new SessionGrantService(sweep, new FixedClock(now)).SweepOrphansAsync(ct));
        await using var read = GetTenantedSession();
        Assert.Equal(keepId, Assert.Single(await read.Query<SessionRevocation>().ToListAsync(ct)).Id);
    }

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
