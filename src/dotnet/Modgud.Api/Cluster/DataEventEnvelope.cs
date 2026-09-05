using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.EventDispatcher;
using Modgud.Domain.Common;

namespace Modgud.Api.Cluster;

/// <summary>
/// Wire form of a <see cref="DataEvent"/> between nodes (ADR 0010, D5): the event
/// type of the SignalARRR cluster subject that carries Modgud's live updates.
/// Owned by Modgud so that a peer on the previous release can still read it.
/// <para>
/// Payload objects are the projection documents and DTOs the hubs map (for
/// example <c>UserView</c> → DTO), so they travel with their CLR type name and
/// are rehydrated into the same types on the receiving node. Only types from
/// this deployment's own assemblies are ever resolved; anything else fails
/// decoding. During a rolling update a peer may run a different build: a
/// payload that no longer deserialises is reported by the caller and dropped,
/// the grid catches up on its next fetch, and nothing else is affected.
/// </para>
/// </summary>
public sealed class DataEventEnvelope
{
    /// <summary>Serializer for the payload items and the metadata (Modgud's own document conventions).</summary>
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new OptionalAwareTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(), new OptionalJsonConverterFactory() },
    };

    /// <summary>
    /// Serializer the cluster subject uses for the envelope itself. Enums travel
    /// as names so that an added <see cref="DataEventAction"/> is still readable by
    /// a peer that knows it, and unknown ones fail loudly instead of mapping to
    /// the wrong number.
    /// </summary>
    public static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] TrustedAssemblyPrefixes = ["Modgud.", "BuildingBlocks."];

    /// <summary>Id of the node that raised the event; a node ignores its own envelopes.</summary>
    public string Node { get; set; } = "";
    public DataEventAction Action { get; set; }
    public string? CustomAction { get; set; }
    public string Subject { get; set; } = "";
    public string? Tenant { get; set; }
    public JsonElement? MetaData { get; set; }
    public Item[] Payload { get; set; } = [];

    public sealed class Item
    {
        public string Type { get; set; } = "";
        public JsonElement Json { get; set; }
    }

    public static DataEventEnvelope Encode(DataEvent @event, string nodeId) => new()
    {
        Node = nodeId,
        Action = @event.Action,
        CustomAction = @event.CustomAction,
        Subject = @event.Subject,
        Tenant = @event.Tenant,
        MetaData = @event.MetaData.Count == 0 ? null : JsonSerializer.SerializeToElement(@event.MetaData, PayloadJson),
        Payload = @event.Payload.Select(p => new Item
        {
            Type = p.GetType().AssemblyQualifiedName!,
            Json = JsonSerializer.SerializeToElement(p, p.GetType(), PayloadJson),
        }).ToArray(),
    };

    /// <summary>
    /// Rehydrates a peer's envelope. Returns <c>null</c> when the envelope was
    /// raised by <paramref name="localNodeId"/> itself. Throws when a payload
    /// type is not one of this deployment's own or cannot be deserialised.
    /// </summary>
    public static DataEvent? Decode(DataEventEnvelope envelope, string localNodeId)
    {
        if (string.Equals(envelope.Node, localNodeId, StringComparison.Ordinal))
            return null;

        var payload = new List<object>(envelope.Payload.Length);
        foreach (var item in envelope.Payload)
        {
            var type = ResolveTrustedType(item.Type)
                ?? throw new InvalidOperationException($"Payload type '{item.Type}' is not a type of this deployment.");
            payload.Add(item.Json.Deserialize(type, PayloadJson)
                ?? throw new JsonException($"Payload of type '{item.Type}' deserialised to null."));
        }

        var dataEvent = new DataEvent(envelope.Action, envelope.Subject, payload)
        {
            CustomAction = envelope.CustomAction,
            Tenant = envelope.Tenant,
        };
        if (envelope.MetaData is { } meta)
        {
            foreach (var prop in meta.EnumerateObject())
                dataEvent.MetaData[prop.Name] = prop.Value;
        }
        return dataEvent;
    }

    /// <summary>
    /// Plain values the dispatchers send as payload next to documents: the id of a
    /// deleted entity (<c>string</c>, <c>Guid</c>), counters, flags. Closed list —
    /// a BCL type outside it is refused like any foreign type.
    /// </summary>
    private static readonly HashSet<Type> TrustedValueTypes =
    [
        typeof(string), typeof(Guid), typeof(bool), typeof(int), typeof(long),
        typeof(decimal), typeof(double), typeof(DateTime), typeof(DateTimeOffset),
    ];

    private static Type? ResolveTrustedType(string assemblyQualifiedName)
    {
        var type = Type.GetType(assemblyQualifiedName, throwOnError: false);
        if (type is null) return null;
        if (TrustedValueTypes.Contains(type)) return type;
        var assembly = type.Assembly.GetName().Name ?? "";
        return TrustedAssemblyPrefixes.Any(prefix => assembly.StartsWith(prefix, StringComparison.Ordinal))
            ? type
            : null;
    }
}
