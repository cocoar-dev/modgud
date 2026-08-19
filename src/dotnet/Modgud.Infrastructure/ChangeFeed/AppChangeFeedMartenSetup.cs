using Marten;

namespace Modgud.Infrastructure.ChangeFeed;

public static class AppChangeFeedMartenSetup
{
    public static StoreOptions UseAppChangeFeed(this StoreOptions options)
    {
        options.Schema.For<AppChangeFeedState>()
            .Identity(x => x.Id);

        options.Schema.For<AppChangeFeedEntityState>()
            .Identity(x => x.Id)
            .Index(
                x => new { x.AppId, x.EntityKind, x.EntityId },
                x => x.Name = "idx_app_feed_entity");

        options.Schema.For<AppChangeFeedEntry>()
            .Identity(x => x.Id)
            .Index(
                x => new { x.AppId, x.Generation, x.SourceSequence, x.Ordinal },
                x => x.Name = "idx_app_feed_cursor")
            .Index(x => x.RecordedAt, x => x.Name = "idx_app_feed_recorded");

        return options;
    }
}
