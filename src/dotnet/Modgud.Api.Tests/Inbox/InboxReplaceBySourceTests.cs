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
