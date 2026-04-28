using System.Reflection;
using JasperFx.Events;
using Marten;
using Wolverine;

namespace TimeToDo.Api.Features.Shared;

/// <summary>
/// Base class for denormalized reference sync handlers.
/// Enforces the correct patterns for Wolverine Event Forwarding:
/// - IEvent&lt;T&gt; wrapper (required for Event Forwarding)
/// - ShouldSync check (early exit if no relevant changes)
/// - SaveChangesAsync (auto-called after sync)
/// - Structured logging
///
/// Usage: Inherit and implement ShouldSync + SyncAsync.
/// Wolverine discovers the Handle method automatically.
/// </summary>
public abstract class ReferenceSyncHandler<TEvent> where TEvent : class
{
    protected ILogger Logger { get; }

    protected ReferenceSyncHandler(ILogger logger) => Logger = logger;

    public async Task Handle(IEvent<TEvent> eventEnvelope, IDocumentSession session)
    {
        var @event = eventEnvelope.Data;

        if (!ShouldSync(@event))
        {
            Logger.LogDebug("[{Handler}] No relevant changes, skipping", GetType().Name);
            return;
        }

        Logger.LogInformation("[{Handler}] Starting sync", GetType().Name);

        await SyncAsync(@event, session);
        await session.SaveChangesAsync();

        Logger.LogInformation("[{Handler}] Sync completed", GetType().Name);
    }

    /// <summary>
    /// Return true if this event contains changes that require a sync.
    /// Called before any DB access — use for cheap checks on event fields.
    /// </summary>
    protected abstract bool ShouldSync(TEvent @event);

    /// <summary>
    /// Execute the actual patch operations. SaveChangesAsync is called automatically after this.
    /// </summary>
    protected abstract Task SyncAsync(TEvent @event, IDocumentSession session);
}

/// <summary>
/// Auto-registers Wolverine PublishMessage subscriptions for all ReferenceSyncHandler&lt;TEvent&gt; implementations.
/// Scans the assembly for concrete handlers, extracts the TEvent type parameter,
/// and routes each unique event type to the "reference-sync" local durable queue.
/// </summary>
public static class ReferenceSyncRegistration
{
    public static void RegisterAll(WolverineOptions opts, Assembly assembly)
    {
        var eventTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.BaseType is { IsGenericType: true }
                && t.BaseType.GetGenericTypeDefinition() == typeof(ReferenceSyncHandler<>))
            .Select(t => t.BaseType!.GetGenericArguments()[0])
            .Distinct();

        foreach (var eventType in eventTypes)
        {
            // opts.PublishMessage<TEvent>().ToLocalQueue("reference-sync").UseDurableInbox()
            // via reflection since the event type is determined at runtime
            var method = typeof(WolverineOptions).GetMethod(nameof(WolverineOptions.PublishMessage), Type.EmptyTypes)!
                .MakeGenericMethod(eventType);
            var subscriber = method.Invoke(opts, null);

            var toLocalQueue = subscriber!.GetType().GetMethod("ToLocalQueue")!;
            var queue = toLocalQueue.Invoke(subscriber, ["reference-sync"]);

            var useDurable = queue!.GetType().GetMethod("UseDurableInbox", Type.EmptyTypes)!;
            useDurable.Invoke(queue, null);
        }
    }
}
