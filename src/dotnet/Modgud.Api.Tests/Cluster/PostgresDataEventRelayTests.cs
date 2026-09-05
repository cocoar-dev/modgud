using BuildingBlocks.EventDispatcher;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Api.Cluster;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Cluster;

/// <summary>
/// Two relay instances on the shared test database play two nodes (ADR 0010,
/// D5): an event dispatched on one arrives at the other's dispatcher, small
/// events travel inline, large ones through the unlogged table, and a node never
/// receives its own events back.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PostgresDataEventRelayTests : IntegrationTestBase
{
    public PostgresDataEventRelayTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(64)]       // inline NOTIFY payload
    [InlineData(64 * 1024)] // above the 8 kB notification limit → messages table
    public async Task Event_dispatched_on_node_a_arrives_on_node_b(int payloadSize)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = Factory.Services.GetRequiredService<IMasterConnectionString>().Value;

        var receivedOnB = new TaskCompletionSource<DataEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedOnA = 0;
        DataEventDispatcher dispatcherA = null!, dispatcherB = null!;
        await using var relayA = new PostgresDataEventRelay(connectionString, "node-a", () => dispatcherA, NullLogger<PostgresDataEventRelay>.Instance);
        await using var relayB = new PostgresDataEventRelay(connectionString, "node-b", () => dispatcherB, NullLogger<PostgresDataEventRelay>.Instance);
        dispatcherA = new DataEventDispatcher(relayA);
        dispatcherB = new DataEventDispatcher(relayB);
        using var _ = dispatcherA.Notifications.Subscribe(_ => receivedOnA++);
        using var __ = dispatcherB.Notifications.Subscribe(ev => receivedOnB.TrySetResult(ev));

        await relayA.StartAsync(ct);
        await relayB.StartAsync(ct);
        await Task.Delay(500, ct); // both listeners subscribed

        var payload = new Note(new string('x', payloadSize));
        dispatcherA.DispatchUpdatedEvent("Note", payload, "acme");

        var onB = await receivedOnB.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("Note", onB.Subject);
        Assert.Equal("acme", onB.Tenant);
        Assert.Equal(payloadSize, Assert.IsType<Note>(Assert.Single(onB.Payload)).Text.Length);

        // The local subscriber saw it once (the dispatch), never a second time from the relay.
        await Task.Delay(300, ct);
        Assert.Equal(1, receivedOnA);

        await relayA.StopAsync(ct);
        await relayB.StopAsync(ct);
    }

    public sealed record Note(string Text);
}
