using BuildingBlocks.EventDispatcher;

namespace Modgud.Tests.Unit.Cluster;

/// <summary>
/// The dispatcher's cross-node seam (ADR 0010, D5): a locally raised event is
/// delivered to local subscribers AND handed to the relay; an event replayed
/// from a peer is delivered locally and never relayed again (no ping-pong);
/// a failing relay never reaches the producer.
/// </summary>
public class DataEventDispatcherRelayTests
{
    [Fact]
    public async Task Local_dispatch_notifies_subscribers_and_relays()
    {
        var relay = new RecordingRelay();
        var sut = new DataEventDispatcher(relay);
        var received = new List<DataEvent>();
        using var _ = sut.Notifications.Subscribe(received.Add);

        sut.DispatchUpdatedEvent("User", new { Id = 1 }, "acme");

        var relayed = await relay.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(received);
        Assert.Equal("User", relayed.Subject);
        Assert.Equal("acme", relayed.Tenant);
        Assert.Same(received[0], relayed);
    }

    [Fact]
    public void Remote_dispatch_notifies_subscribers_but_does_not_relay()
    {
        var relay = new RecordingRelay();
        var sut = new DataEventDispatcher(relay);
        var received = new List<DataEvent>();
        using var _ = sut.Notifications.Subscribe(received.Add);

        sut.DispatchRemoteEvent(DataEvent.Created("Position", new { Id = 2 }).WithTenant("acme"));

        Assert.Single(received);
        Assert.False(relay.Published.Task.IsCompleted);
    }

    [Fact]
    public async Task Relay_failure_is_reported_and_never_thrown_at_the_producer()
    {
        var sut = new DataEventDispatcher(new FailingRelay());
        var failed = new TaskCompletionSource<Exception>();
        sut.RelayFailed += (_, ex) => failed.TrySetResult(ex);

        sut.DispatchDeletedEvent("User", "1", "acme"); // must not throw

        var ex = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Default_relay_is_a_no_op()
    {
        var sut = new DataEventDispatcher();
        var received = 0;
        using var _ = sut.Notifications.Subscribe(_ => received++);

        sut.DispatchCreatedEvent("User", new { Id = 3 });

        Assert.Equal(1, received);
    }

    private sealed class RecordingRelay : IDataEventRelay
    {
        public TaskCompletionSource<DataEvent> Published { get; } = new();

        public ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
        {
            Published.TrySetResult(@event);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingRelay : IDataEventRelay
    {
        public ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("relay down");
    }
}
