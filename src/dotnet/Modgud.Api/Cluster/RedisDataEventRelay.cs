using BuildingBlocks.EventDispatcher;
using StackExchange.Redis;

namespace Modgud.Api.Cluster;

/// <summary>
/// Cross-node transport for <see cref="DataEvent"/>s over the same Valkey/Redis
/// the SignalARRR Redis backplane runs on (ADR 0010, D5). Used only when the
/// operator chose the Redis backplane; the default is <see cref="PostgresDataEventRelay"/>.
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
/// </summary>
public sealed class RedisDataEventRelay : IDataEventRelay, IHostedService, IAsyncDisposable
{
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
        _logger.LogInformation("Data-event relay (Redis) connected — channel {Channel}, node {Node}", _channel, _nodeId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscriber is not null)
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_channel));
    }

    public async ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
    {
        if (_subscriber is null) return;
        await _subscriber.PublishAsync(RedisChannel.Literal(_channel), DataEventEnvelope.Encode(@event, _nodeId));
    }

    private void OnMessage(RedisChannel _, RedisValue value)
    {
        try
        {
            var dataEvent = DataEventEnvelope.Decode((string)value!, _nodeId);
            if (dataEvent is null) return;
            _services.GetRequiredService<DataEventDispatcher>().DispatchRemoteEvent(dataEvent);
        }
        catch (Exception ex)
        {
            // A peer on another build may send a shape we cannot read; the grid
            // catches up on its next fetch. Never let one message kill the subscription.
            _logger.LogWarning(ex, "Dropping a relayed data event that could not be rehydrated");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
    }
}
