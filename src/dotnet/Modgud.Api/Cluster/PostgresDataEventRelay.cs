using System.Text;
using System.Threading.Channels;
using BuildingBlocks.EventDispatcher;
using Npgsql;

namespace Modgud.Api.Cluster;

/// <summary>
/// Cross-node transport for <see cref="DataEvent"/>s over the master database
/// (ADR 0010, D5) — the one thing a second Modgud instance needs beyond
/// PostgreSQL, so a two-instance deployment has no second stateful service.
/// <para>
/// <c>NOTIFY</c> on one channel, one long-lived <c>LISTEN</c> connection per
/// node with keep-alive and reconnect, envelopes above the notification limit
/// written to an unlogged table in the same transaction as the <c>NOTIFY</c>,
/// which then carries only the row id. Delivery is transient by contract (a
/// node whose listener is reconnecting misses what was published in between;
/// the grid catches up on its next fetch), so the table is unlogged and swept
/// after two minutes.
/// </para>
/// <para>
/// Why not a SignalR backplane: every hub in Modgud is a server stream fed by
/// the in-process <see cref="DataEventDispatcher"/> observable; there are no
/// targeted sends, and a backplane only routes those. Making the observable
/// cluster-wide is the whole job, and each browser still receives every event
/// exactly once, from the node its connection is pinned to.
/// </para>
/// </summary>
// CA2100: every statement below is a compile-time constant (schema and channel
// names are const); values travel as parameters, never as concatenated input.
#pragma warning disable CA2100
public sealed class PostgresDataEventRelay : IDataEventRelay, IHostedService, IAsyncDisposable
{
    public const string Schema = "modgud_cluster";
    public const string Channel = "modgud_data_events";

    // Postgres rejects notification payloads of 8000 bytes or more.
    internal const int MaxInlinePayloadBytes = 7500;
    private static readonly TimeSpan MessageRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectMinDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(10);

    private readonly string _connectionString;
    private readonly string _listenerConnectionString;
    private readonly string _nodeId;
    private readonly Func<DataEventDispatcher> _dispatcher;
    private readonly ILogger<PostgresDataEventRelay> _logger;
    private readonly Channel<string> _notifications = System.Threading.Channels.Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private Task? _consumerTask;
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public PostgresDataEventRelay(
        string masterConnectionString,
        string nodeId,
        Func<DataEventDispatcher> dispatcher,
        ILogger<PostgresDataEventRelay> logger)
    {
        _connectionString = masterConnectionString;
        // The listener sits in WaitAsync with nothing else ever written to it;
        // keep-alive is what tells us the socket died.
        _listenerConnectionString = new NpgsqlConnectionStringBuilder(masterConnectionString)
        {
            KeepAlive = 30,
            Pooling = false,
        }.ConnectionString;
        _nodeId = nodeId;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Idempotent DDL, applied on start (the role needs CREATE on the database).</summary>
    public static string GetCreateScript() => $"""
        CREATE SCHEMA IF NOT EXISTS {Schema};
        CREATE UNLOGGED TABLE IF NOT EXISTS {Schema}.data_events (
            id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            created_at  timestamptz NOT NULL DEFAULT now(),
            payload     text NOT NULL
        );
        """;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            // Two nodes booting together must not both create the schema:
            // CREATE ... IF NOT EXISTS is not atomic across sessions and the
            // loser fails on a catalog uniqueness violation. Serialise on a
            // transaction-scoped advisory lock, released with the commit.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", connection, transaction))
            {
                lockCommand.Parameters.Add(new NpgsqlParameter { Value = $"{Schema}.data_events" });
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = new NpgsqlCommand(GetCreateScript(), connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        _cts = new CancellationTokenSource();
        _listenerTask = RunListenerAsync(_cts.Token);
        _consumerTask = RunConsumerAsync(_cts.Token);
        _logger.LogInformation("Data-event relay (Postgres) started — channel {Channel}, node {Node}", Channel, _nodeId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        _cts.Cancel();
        _notifications.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_listenerTask ?? Task.CompletedTask, _consumerTask ?? Task.CompletedTask)
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // shutting down anyway
        }
    }

    public async ValueTask PublishAsync(DataEvent @event, CancellationToken cancellationToken = default)
    {
        var payload = DataEventEnvelope.Encode(@event, _nodeId);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (Encoding.UTF8.GetByteCount(payload) <= MaxInlinePayloadBytes)
        {
            await using var notify = new NpgsqlCommand("SELECT pg_notify($1, $2)", connection);
            notify.Parameters.Add(new NpgsqlParameter { Value = Channel });
            notify.Parameters.Add(new NpgsqlParameter { Value = payload });
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // INSERT and NOTIFY in one statement, hence one transaction: the
            // notification is delivered at commit, when the row is visible.
            await using var notify = new NpgsqlCommand(
                $"WITH m AS (INSERT INTO {Schema}.data_events (payload) VALUES ($2) RETURNING id) " +
                "SELECT pg_notify($1, '#' || m.id::text) FROM m", connection);
            notify.Parameters.Add(new NpgsqlParameter { Value = Channel });
            notify.Parameters.Add(new NpgsqlParameter { Value = payload });
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }

        await SweepAsync(connection, cancellationToken);
    }

    private async Task SweepAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < SweepInterval) return;
        _lastSweep = now;
        await using var sweep = new NpgsqlCommand(
            $"DELETE FROM {Schema}.data_events WHERE created_at < now() - $1", connection);
        sweep.Parameters.Add(new NpgsqlParameter { Value = MessageRetention });
        await sweep.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RunListenerAsync(CancellationToken cancellationToken)
    {
        var delay = ReconnectMinDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_listenerConnectionString);
                void OnNotification(object sender, NpgsqlNotificationEventArgs e) => _notifications.Writer.TryWrite(e.Payload);
                connection.Notification += OnNotification;
                try
                {
                    await connection.OpenAsync(cancellationToken);
                    await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                        await listen.ExecuteNonQueryAsync(cancellationToken);
                    delay = ReconnectMinDelay;
                    while (!cancellationToken.IsCancellationRequested)
                        await connection.WaitAsync(cancellationToken);
                }
                finally
                {
                    connection.Notification -= OnNotification;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Data-event relay lost its LISTEN connection; events until it reconnects are lost. Reconnecting in {Delay}",
                    delay);
                try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ReconnectMaxDelay.Ticks));
            }
        }
    }

    private async Task RunConsumerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _notifications.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var json = payload.StartsWith('#')
                        ? await LoadStoredAsync(long.Parse(payload.AsSpan(1)), cancellationToken)
                        : payload;
                    if (json is null) continue;

                    var dataEvent = DataEventEnvelope.Decode(json, _nodeId);
                    if (dataEvent is null) continue;
                    _dispatcher().DispatchRemoteEvent(dataEvent);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A peer on another build may send a shape we cannot read; the
                    // grid catches up on its next fetch. Never let one message kill
                    // the consumer.
                    _logger.LogWarning(ex, "Dropping a relayed data event that could not be rehydrated");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task<string?> LoadStoredAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT payload FROM {Schema}.data_events WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        await ValueTask.CompletedTask;
    }
}
#pragma warning restore CA2100
