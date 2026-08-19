using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;

namespace Modgud.Tests.Unit.Authorization.Principals;

public class ServiceAccountProjectionTests
{
    [Fact]
    public void Full_lifecycle_replays_to_soft_deleted_inactive_state()
    {
        var id = Guid.NewGuid();
        var projection = new ServiceAccountProjection();

        var account = projection.Apply(
            new ServiceAccountCreatedEvent(id, "sync-agent", "Initial import", true),
            new ServiceAccount());
        projection.Apply(
            new ServiceAccountUpdatedEvent(id, "sync-agent-v2", "Change feed", true),
            account);
        projection.Apply(new ServiceAccountDeletedEvent(id), account);

        Assert.Equal(id, account.Id);
        Assert.Equal("sync-agent-v2", account.AccountName);
        Assert.Equal("Change feed", account.Purpose);
        Assert.False(account.IsActive);
        Assert.True(account.IsDeleted);
    }

    [Fact]
    public void Creation_replaces_a_legacy_snapshot_during_teardown_free_rebuild()
    {
        var id = Guid.NewGuid();
        var legacy = new ServiceAccount
        {
            Id = id,
            AccountName = "stale-name",
            Purpose = "stale-purpose",
            IsActive = false,
            IsDeleted = true,
        };

        var rebuilt = new ServiceAccountProjection().Apply(
            new ServiceAccountCreatedEvent(id, "canonical-name", null, true),
            legacy);

        Assert.NotSame(legacy, rebuilt);
        Assert.Equal(id, rebuilt.Id);
        Assert.Equal("canonical-name", rebuilt.AccountName);
        Assert.Null(rebuilt.Purpose);
        Assert.True(rebuilt.IsActive);
        Assert.False(rebuilt.IsDeleted);
    }
}
