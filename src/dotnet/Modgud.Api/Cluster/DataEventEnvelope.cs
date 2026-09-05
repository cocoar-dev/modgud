using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.EventDispatcher;
using Modgud.Domain.Common;

namespace Modgud.Api.Cluster;

/// <summary>
/// Wire form of a <see cref="DataEvent"/> between nodes (ADR 0010, D5), shared by
/// every relay transport. Payload objects are the projection documents and DTOs
/// the hubs map (for example <c>UserView</c> → DTO), so they travel with their
/// CLR type name and are rehydrated into the same types on the receiving node.
/// Only types from this deployment's own assemblies are ever resolved; anything
/// else fails decoding. During a rolling update a peer may run a different
/// build: a payload that no longer deserialises is reported by the caller and
/// dropped, the grid catches up on its next fetch, and nothing else is affected.
/// </summary>
public static class DataEventEnvelope
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new OptionalAwareTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(), new OptionalJsonConverterFactory() },
    };

    private static readonly string[] TrustedAssemblyPrefixes = ["Modgud.", "BuildingBlocks."];

    public static string Encode(DataEvent @event, string nodeId)
    {
        var envelope = new Envelope
        {
            Node = nodeId,
            Action = @event.Action,
            CustomAction = @event.CustomAction,
            Subject = @event.Subject,
            Tenant = @event.Tenant,
            MetaData = @event.MetaData.Count == 0 ? null : JsonSerializer.SerializeToElement(@event.MetaData, Json),
            Payload = @event.Payload.Select(p => new Item
            {
                Type = p.GetType().AssemblyQualifiedName!,
                Json = JsonSerializer.SerializeToElement(p, p.GetType(), Json),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(envelope, Json);
    }

    /// <summary>
    /// Decodes a peer's envelope. Returns <c>null</c> when the message came from
    /// <paramref name="localNodeId"/> itself. Throws when a payload type is not
    /// one of this deployment's own or cannot be deserialised.
    /// </summary>
    public static DataEvent? Decode(string json, string localNodeId)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, Json)
            ?? throw new JsonException("Empty data-event envelope.");
        if (string.Equals(envelope.Node, localNodeId, StringComparison.Ordinal))
            return null;

        var payload = new List<object>(envelope.Payload.Length);
        foreach (var item in envelope.Payload)
        {
            var type = ResolveTrustedType(item.Type)
                ?? throw new InvalidOperationException($"Payload type '{item.Type}' is not a type of this deployment.");
            payload.Add(item.Json.Deserialize(type, Json)
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

    private static Type? ResolveTrustedType(string assemblyQualifiedName)
    {
        var type = Type.GetType(assemblyQualifiedName, throwOnError: false);
        if (type is null) return null;
        var assembly = type.Assembly.GetName().Name ?? "";
        return TrustedAssemblyPrefixes.Any(prefix => assembly.StartsWith(prefix, StringComparison.Ordinal))
            ? type
            : null;
    }

    private sealed class Envelope
    {
        public string Node { get; set; } = "";
        public DataEventAction Action { get; set; }
        public string? CustomAction { get; set; }
        public string Subject { get; set; } = "";
        public string? Tenant { get; set; }
        public JsonElement? MetaData { get; set; }
        public Item[] Payload { get; set; } = [];
    }

    private sealed class Item
    {
        public string Type { get; set; } = "";
        public JsonElement Json { get; set; }
    }
}
