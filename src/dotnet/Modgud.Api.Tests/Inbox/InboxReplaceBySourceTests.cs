using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.Inbox;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Api.Tests.Inbox;

/// <summary>
/// B3-EI-06 — a ReplaceBySource kind keeps exactly one open item per (recipient, source).
/// The dedup read the asynchronous inbox view; two notifications before it caught up both
/// saw no predecessor and both stayed open. The test host has no background daemon, so two
/// calls without a catch-up in between are exactly that lag.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class InboxReplaceBySourceTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>B3-EI-04 — dismissing by source read the same lagging view: an item created a
    /// moment before (an admin notification still in flight when the request was decided) stayed
    /// open. The source slot is written with the item, so it is found at once; the kind filter
    /// still applies.</summary>
    [Fact]
    public async Task Dismissing_by_source_before_the_view_catches_up_dismisses_the_new_item_of_that_kind_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        Guid itemId;
        using (var scope = Factory.Services.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
            itemId = (await inbox.NotifyAsync(InboxKind.AdminChangeRequestSubmitted, [DefaultUser!.Id],
                "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = "Fresh", FieldList = "Lastname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: sourceId, ct: ct)).Single();
            await inbox.DismissBySourceAsync("change-request", sourceId, InboxKind.ChangeRequestApproved, ct);
        }
        await Factory.WaitForProjectionsAsync();
        using (var read = Factory.Services.CreateScope())
            Assert.Null((await read.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<InboxItemView>(itemId, ct))!.DismissedAt);

        using (var scope = Factory.Services.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
            var replaced = (await inbox.NotifyAsync(InboxKind.AdminChangeRequestSubmitted, [DefaultUser!.Id],
                "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = "Fresher", FieldList = "Lastname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: sourceId, ct: ct)).Single();
            await inbox.DismissBySourceAsync("change-request", sourceId, InboxKind.AdminChangeRequestSubmitted, ct);
            itemId = replaced;
        }
        await Factory.WaitForProjectionsAsync();
        using var after = Factory.Services.CreateScope();
        Assert.NotNull((await after.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<InboxItemView>(itemId, ct))!.DismissedAt);
    }

    [Fact]
    public async Task Two_notifications_before_the_view_catches_up_leave_one_open_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipient = DefaultUser!.Id;
        var sourceId = Guid.NewGuid();

        for (var i = 0; i < 2; i++)
        {
            using var scope = Factory.Services.CreateScope();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
            await inbox.NotifyAsync(InboxKind.AdminChangeRequestSubmitted, [recipient],
                "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = $"Lag {i}", FieldList = "Lastname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: sourceId, ct: ct);
        }

        await Factory.WaitForProjectionsAsync();
        using var read = Factory.Services.CreateScope();
        var session = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var items = await session.Query<InboxItemView>()
            .Where(x => x.RecipientUserId == recipient && x.SourceType == "change-request" && x.SourceId == sourceId)
            .ToListAsync(ct);

        Assert.Equal(2, items.Count);
        Assert.Single(items, x => x.DismissedAt is null);
    }

    [Fact]
    public async Task Concurrent_notifications_for_one_source_leave_one_open_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipient = DefaultUser!.Id;
        var sourceId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            using var scope = Factory.Services.CreateScope();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
            await inbox.NotifyAsync(InboxKind.AdminChangeRequestSubmitted, [recipient],
                "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = $"Race {i}", FieldList = "Lastname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: sourceId, ct: ct);
        }));

        await Factory.WaitForProjectionsAsync();
        using var read = Factory.Services.CreateScope();
        var session = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var open = await session.Query<InboxItemView>()
            .Where(x => x.RecipientUserId == recipient && x.SourceId == sourceId && x.DismissedAt == null)
            .CountAsync(ct);

        Assert.Equal(1, open);
    }
}
