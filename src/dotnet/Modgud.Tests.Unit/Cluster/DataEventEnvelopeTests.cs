using BuildingBlocks.EventDispatcher;
using Modgud.Api.Cluster;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Tests.Unit.Cluster;

/// <summary>
/// The wire form shared by both data-event relays (ADR 0010, D5): a typed payload
/// round-trips into the same CLR type, the sender's own messages are recognised,
/// and a payload type outside this deployment's assemblies is refused.
/// </summary>
public class DataEventEnvelopeTests
{
    [Fact]
    public void Round_trips_a_typed_payload_and_tenant()
    {
        var view = new UserView { Id = Guid.NewGuid(), UserName = "alice", Email = "alice@example.test" };
        var original = DataEvent.Updated("User", view).WithTenant("acme");

        var json = DataEventEnvelope.Encode(original, "node-a");
        var decoded = DataEventEnvelope.Decode(json, "node-b");

        Assert.NotNull(decoded);
        Assert.Equal(DataEventAction.Updated, decoded!.Action);
        Assert.Equal("User", decoded.Subject);
        Assert.Equal("acme", decoded.Tenant);
        var payload = Assert.IsType<UserView>(Assert.Single(decoded.Payload));
        Assert.Equal(view.Id, payload.Id);
        Assert.Equal("alice", payload.UserName);
    }

    [Fact]
    public void Own_messages_decode_to_null()
    {
        var json = DataEventEnvelope.Encode(DataEvent.Deleted("User", "1"), "node-a");

        Assert.Null(DataEventEnvelope.Decode(json, "node-a"));
    }

    [Fact]
    public void Foreign_payload_types_are_refused()
    {
        var json = DataEventEnvelope.Encode(DataEvent.Created("Thing", new Uri("https://example.test/")), "node-a");

        Assert.Throws<InvalidOperationException>(() => DataEventEnvelope.Decode(json, "node-b"));
    }
}
