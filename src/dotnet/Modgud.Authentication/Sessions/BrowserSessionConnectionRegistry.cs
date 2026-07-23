using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Modgud.Authentication.Sessions;

public interface IBrowserSessionConnectionRegistry
{
    IDisposable Register(Guid sessionId, string connectionId, HttpContext httpContext);
    void Revoke(Guid sessionId);
}

/// <summary>
/// Process-local registry used to abort already-upgraded SignalR connections
/// immediately when their authoritative browser session is revoked. In a
/// multi-node deployment the normal per-invocation validation remains the
/// cross-node backstop; a distributed disconnect notification can be added with
/// the SignalR backplane.
/// </summary>
public sealed class BrowserSessionConnectionRegistry : IBrowserSessionConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HttpContext>> _connections = new();

    public IDisposable Register(Guid sessionId, string connectionId, HttpContext httpContext)
    {
        var perSession = _connections.GetOrAdd(sessionId, _ => new());
        perSession[connectionId] = httpContext;
        return new Registration(this, sessionId, connectionId);
    }

    public void Revoke(Guid sessionId)
    {
        if (!_connections.TryRemove(sessionId, out var connections)) return;
        foreach (var http in connections.Values)
            http.Abort();
    }

    private void Remove(Guid sessionId, string connectionId)
    {
        if (!_connections.TryGetValue(sessionId, out var connections)) return;
        connections.TryRemove(connectionId, out _);
        if (connections.IsEmpty)
            _connections.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, HttpContext>>(sessionId, connections));
    }

    private sealed class Registration(
        BrowserSessionConnectionRegistry owner,
        Guid sessionId,
        string connectionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Remove(sessionId, connectionId);
        }
    }
}
