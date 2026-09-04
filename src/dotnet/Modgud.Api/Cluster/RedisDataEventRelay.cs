using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.EventDispatcher;
using Modgud.Domain.Common;
using StackExchange.Redis;

namespace Modgud.Api.Cluster;

/// <summary>
/// Cross-node transport for <see cref="DataEvent"/>s over the same Valkey/Redis
/// the SignalARRR backplane runs on (ADR 0010, D5).
/// <para>
/// Every hub stream in Modgud (<c>UserActions</c>, <c>PositionActions</c>,
/// <c>InviteCodeActions</c>, …) is fed by the in-process
/// <see cref="DataEventDispatcher"/>. The SignalARRR backplane routes targeted
/// sends across nodes, but it cannot know about an observable that only exists
/// in one process: an event raised by a request on node A would never reach a
/// grid subscribed on node B. This relay publishes each locally raised event and
/// replays events from peers into the local dispatcher, so the observable is
/// cluster-wide while the hubs stay untouched.
/// </para>
/// <para>
/// Payload objects are the projection documents and DTOs the hubs map (for
/// example <c>UserView</c> → DTO), so they travel with their CLR type name and
/// are rehydrated into the same types on the receiving node. Only types from
/// this deployment's own assemblies are ever resolved; anything else is dropped
/// and logged. During a rolling update a peer may run a different build: a
/// payload that no longer deserialises is dropped, the grid catches up on its
/// next fetch, and nothing else is affected.
/// </para>
/// </summary>
public sealed class RedisDataEventRelay : IDataEventRelay, IHostedService, IAsyncDisposable
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

    private readonly string _connectionString;
    private readonly string _channel;
    private readonly string _nodeId;
    private readonly IServiceProvider _services;
    private readonly ILogger<RedisDataEventRelay> _logger;
    private ConnectionMultiplexer? _redis;
    private ISubscriber? _subscriber;

    public RedisDataEventRelay(
        ClusterSettings settings,
        string nodeId,
        IServiceProvider services,
        ILogger<RedisDataEventRelay> logger)
    {
        _connectionString = settings.Backplane.ConnectionString;
        _channel = $"{settings.Backplane.ChannelPrefix}:data-events";
        _nodeId = nodeId;
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(_connectionString);
        _subscriber = _redis.GetSubscriber();
        await _subscriber.SubscribeAsync(RedisChannel.Literal(_channel), OnMessage);
        _logger.LogInformation("Data-event relay connected — channel {Channel}, node {Node}", _channel, _nodeId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscriber is not null)
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_channel));
    }

    public async ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
    {
        if (_subscriber is null) return;

        var envelope = new Envelope
        {
            Node = _nodeId,
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

        await _subscriber.PublishAsync(RedisChannel.Literal(_channel), JsonSerializer.Serialize(envelope, Json));
    }

    private void OnMessage(RedisChannel _, RedisValue value)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>((string)value!, Json);
            if (envelope is null || string.Equals(envelope.Node, _nodeId, StringComparison.Ordinal))
                return;

            var payload = new List<object>(envelope.Payload.Length);
            foreach (var item in envelope.Payload)
            {
                var type = ResolveTrustedType(item.Type);
                if (type is null)
                {
                    _logger.LogWarning("Dropping relayed {Subject} event: payload type {Type} is not a type of this deployment", envelope.Subject, item.Type);
                    return;
                }
                var obj = item.Json.Deserialize(type, Json);
                if (obj is null) return;
                payload.Add(obj);
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

            _services.GetRequiredService<DataEventDispatcher>().DispatchRemoteEvent(dataEvent);
        }
        catch (Exception ex)
        {
            // A peer on another build may send a shape we cannot read; the grid
            // catches up on its next fetch. Never let one message kill the subscription.
            _logger.LogWarning(ex, "Dropping a relayed data event that could not be rehydrated");
        }
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

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
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
