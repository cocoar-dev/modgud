using System.Text.Json;
using BuildingBlocks.EventDispatcher;
using Modgud.Api.Cluster;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Tests.Unit.Cluster;

/// <summary>
/// The wire form Modgud's cluster subject carries (ADR 0022, D5): a typed payload
/// survives the subject's own JSON round trip into the same CLR type, the
/// sender's own envelopes are recognised, and a payload type outside this
/// deployment's assemblies is refused.
/// </summary>
public class DataEventEnvelopeTests
{
    [Fact]
    public void Round_trips_a_typed_payload_and_tenant_through_the_wire_serializer()
    {
        var view = new UserView { Id = Guid.NewGuid(), UserName = "alice", Email = "alice@example.test" };
        var original = DataEvent.Updated("User", view).WithTenant("acme");

        var wire = JsonSerializer.Serialize(DataEventEnvelope.Encode(original, "node-a"), DataEventEnvelope.WireJson);
        var envelope = JsonSerializer.Deserialize<DataEventEnvelope>(wire, DataEventEnvelope.WireJson)!;
        var decoded = DataEventEnvelope.Decode(envelope, "node-b");

        Assert.Contains("\"action\":\"Updated\"", wire); // enums travel by name
        Assert.NotNull(decoded);
        Assert.Equal(DataEventAction.Updated, decoded!.Action);
        Assert.Equal("User", decoded.Subject);
        Assert.Equal("acme", decoded.Tenant);
        var payload = Assert.IsType<UserView>(Assert.Single(decoded.Payload));
        Assert.Equal(view.Id, payload.Id);
        Assert.Equal("alice", payload.UserName);
    }

    [Fact]
    public void Deleted_event_with_a_plain_id_payload_round_trips()
    {
        // Every DispatchDeletedEvent sends the entity id as a string, never a document.
        var id = Guid.NewGuid().ToString();
        var wire = JsonSerializer.Serialize(DataEventEnvelope.Encode(DataEvent.Deleted("App", id).WithTenant("acme"), "node-b"), DataEventEnvelope.WireJson);
        var decoded = DataEventEnvelope.Decode(JsonSerializer.Deserialize<DataEventEnvelope>(wire, DataEventEnvelope.WireJson)!, "node-a");

        Assert.NotNull(decoded);
        Assert.Equal(DataEventAction.Deleted, decoded!.Action);
        Assert.Equal(id, Assert.IsType<string>(Assert.Single(decoded.Payload)));
    }

    [Fact]
    public void Own_envelopes_decode_to_null()
    {
        var envelope = DataEventEnvelope.Encode(DataEvent.Deleted("User", "1"), "node-a");

        Assert.Null(DataEventEnvelope.Decode(envelope, "node-a"));
    }

    [Fact]
    public void Foreign_payload_types_are_refused()
    {
        var envelope = DataEventEnvelope.Encode(DataEvent.Created("Thing", new Uri("https://example.test/")), "node-a");

        Assert.Throws<InvalidOperationException>(() => DataEventEnvelope.Decode(envelope, "node-b"));
    }
}
