using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Cocoar.Auth.Application.Contracts;

namespace Cocoar.Auth.Infrastructure.Events;

/// <summary>
/// Implementation of IEventDispatcher that wraps the existing DataEventDispatcher.
/// </summary>
public class SignalREventDispatcher : IEventDispatcher
{
    private readonly DataEventDispatcher _dataEventDispatcher;

    public SignalREventDispatcher(DataEventDispatcher dataEventDispatcher)
    {
        _dataEventDispatcher = dataEventDispatcher;
    }

    public void DispatchCreated<T>(string subject, T payload)
    {
        _dataEventDispatcher.DispatchCreatedEvent(subject, payload);
    }

    public void DispatchUpdated<T>(string subject, T payload)
    {
        _dataEventDispatcher.DispatchUpdatedEvent(subject, payload);
    }

    public void DispatchUpdated<T>(string subject, IEnumerable<T> payload)
    {
        _dataEventDispatcher.DispatchUpdatedEvent(subject, payload.Cast<object>());
    }

    public void DispatchDeleted(string subject, string id)
    {
        _dataEventDispatcher.DispatchDeletedEvent(subject, id);
    }

    public void DispatchDeleted(string subject, IEnumerable<string> ids)
    {
        _dataEventDispatcher.DispatchDeletedEvent(subject, ids);
    }
}
