using System.Reactive.Subjects;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Api.Cluster;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Tests.Unit.Cluster;

/// <summary>
/// The adapter between the dispatcher and the cluster subject (ADR 0010, D5): a
/// local event goes to the subject stamped with this node's id, a peer's envelope
/// is replayed into the dispatcher without being relayed again, the node's own
/// envelope coming back from the subject is skipped (no echo), and an unreadable
/// envelope is dropped without killing the subscription.
/// </summary>
public class ClusterSubjectDataEventRelayTests
{
    [Fact]
    public async Task Local_event_reaches_the_subject_with_this_nodes_id()
    {
        var subject = new FakeSubject();
        var (relay, _) = await Start(subject, "node-a");

        await relay.PublishAsync(DataEvent.Updated("User", new UserView { Id = Guid.NewGuid(), UserName = "alice" }).WithTenant("acme"));

        var envelope = Assert.Single(subject.Published);
        Assert.Equal("node-a", envelope.Node);
        Assert.Equal("User", envelope.Subject);
        Assert.Equal("acme", envelope.Tenant);
    }

    [Fact]
    public async Task Peer_envelope_is_replayed_into_the_dispatcher_and_not_relayed_again()
    {
        var subject = new FakeSubject();
        var (_, dispatcher) = await Start(subject, "node-a");
        var received = new List<DataEvent>();
        using var _ = dispatcher.Notifications.Subscribe(received.Add);

        subject.Push(DataEventEnvelope.Encode(DataEvent.Created("Position", new UserView { Id = Guid.NewGuid() }).WithTenant("acme"), "node-b"));

        var ev = Assert.Single(received);
        Assert.Equal("Position", ev.Subject);
        Assert.Equal("acme", ev.Tenant);
        Assert.Empty(subject.Published);
    }

    [Fact]
    public async Task Own_envelope_from_the_subject_is_skipped()
    {
        var subject = new FakeSubject();
        var (_, dispatcher) = await Start(subject, "node-a");
        var received = 0;
        using var _ = dispatcher.Notifications.Subscribe(_ => received++);

        subject.Push(DataEventEnvelope.Encode(DataEvent.Deleted("User", new UserView { Id = Guid.NewGuid() }), "node-a"));

        Assert.Equal(0, received);
    }

    [Fact]
    public async Task Unreadable_envelope_is_dropped_and_the_next_one_still_arrives()
    {
        var subject = new FakeSubject();
        var (_, dispatcher) = await Start(subject, "node-a");
        var received = new List<DataEvent>();
        using var _ = dispatcher.Notifications.Subscribe(received.Add);

        subject.Push(new DataEventEnvelope
        {
            Node = "node-b",
            Subject = "Thing",
            Payload = [new DataEventEnvelope.Item { Type = typeof(Uri).AssemblyQualifiedName! }],
        });
        subject.Push(DataEventEnvelope.Encode(DataEvent.Deleted("User", new UserView { Id = Guid.NewGuid() }), "node-b"));

        Assert.Equal("User", Assert.Single(received).Subject);
    }

    private static async Task<(ClusterSubjectDataEventRelay Relay, DataEventDispatcher Dispatcher)> Start(FakeSubject subject, string nodeId)
    {
        DataEventDispatcher dispatcher = null!;
        var relay = new ClusterSubjectDataEventRelay(subject, nodeId, () => dispatcher, NullLogger<ClusterSubjectDataEventRelay>.Instance);
        dispatcher = new DataEventDispatcher(relay);
        await relay.StartAsync(CancellationToken.None);
        return (relay, dispatcher);
    }

    private sealed class FakeSubject : IClusterSubject<DataEventEnvelope>
    {
        private readonly Subject<DataEventEnvelope> _inner = new();
        public List<DataEventEnvelope> Published { get; } = [];
        public string Name => ClusterSubjectDataEventRelay.SubjectName;

        // What the real subject does for a peer's event: straight to local subscribers.
        public void Push(DataEventEnvelope envelope) => _inner.OnNext(envelope);

        public void OnNext(DataEventEnvelope value)
        {
            Published.Add(value);
            _inner.OnNext(value); // the real subject notifies local subscribers too
        }

        public Task PublishAsync(DataEventEnvelope value, CancellationToken cancellationToken = default)
        {
            OnNext(value);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(IObserver<DataEventEnvelope> observer) => _inner.Subscribe(observer);
    }
}
