using System.Threading.Channels;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.BackChannelLogout;

/// <summary>
/// ADR 0021 transport A — the prompt first attempt. The fan-out drops the id of a freshly
/// stored <see cref="BackChannelLogoutDelivery"/> here; the dispatcher attempts it within
/// a second on a background thread, off the event-store subscription that produced it.
/// In-memory on purpose: the row in the realm database is the durable record and the
/// per-realm retry job picks up anything that was not delivered — including after a
/// restart.
/// </summary>
public sealed class BackChannelLogoutDispatchQueue
{
    private readonly Channel<(string Realm, Guid DeliveryId)> _channel =
        Channel.CreateUnbounded<(string, Guid)>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(string realm, Guid deliveryId) => _channel.Writer.TryWrite((realm, deliveryId));

    internal ChannelReader<(string Realm, Guid DeliveryId)> Reader => _channel.Reader;
}

public sealed class BackChannelLogoutDispatcher(
    BackChannelLogoutDispatchQueue queue,
    IServiceScopeFactory scopes,
    ILogger<BackChannelLogoutDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (realm, deliveryId) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var _ = TenantContext.Enter(realm);
                using var scope = scopes.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var delivery = await session.LoadAsync<BackChannelLogoutDelivery>(deliveryId, stoppingToken);
                if (delivery is null) continue; // already handled by the retry job, or dropped

                await scope.ServiceProvider.GetRequiredService<IBackChannelLogoutDeliverer>()
                    .AttemptAsync(session, delivery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The retry job owns the row from here on.
                logger.LogWarning(ex, "Immediate back-channel logout attempt for delivery {DeliveryId} in realm {Realm} failed; the retry job will pick it up", deliveryId, realm);
            }
        }
    }
}
