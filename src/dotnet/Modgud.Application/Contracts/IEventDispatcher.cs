namespace Modgud.Application.Contracts;

/// <summary>
/// Application-level abstraction for event dispatching.
/// Infrastructure layer will provide implementation using SignalR/reactive.
/// </summary>
public interface IEventDispatcher
{
    void DispatchCreated<T>(string subject, T payload);
    void DispatchUpdated<T>(string subject, T payload);
    void DispatchUpdated<T>(string subject, IEnumerable<T> payload);
    void DispatchDeleted(string subject, string id);
    void DispatchDeleted(string subject, IEnumerable<string> ids);
}
