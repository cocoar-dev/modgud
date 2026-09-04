using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Modgud.Authentication.Sessions;

public interface IBrowserSessionConnectionRegistry
{
    IDisposable Register(Guid sessionId, string connectionId, HttpContext httpContext);
    void Revoke(Guid sessionId);

    /// <summary>
    /// The browser sessions with at least one live connection on this node,
    /// each with the realm the connection was established in. Read by the
    /// periodic sweep that re-validates them against the database.
    /// </summary>
    IReadOnlyList<BrowserSessionConnection> Snapshot();
}

/// <summary>One browser session that currently holds connections on this node.</summary>
public sealed record BrowserSessionConnection(Guid SessionId, string? Realm);

/// <summary>
/// Process-local registry used to abort already-upgraded SignalR connections
/// immediately when their authoritative browser session is revoked on THIS
/// node. A revocation processed on another node is caught by two DB-driven
/// paths that need no cross-node message (ADR 0010, D6): every hub invocation
/// re-checks the session row, and <c>BrowserSessionConnectionSweeper</c>
/// re-validates every idle connection's session against the database on a
/// short interval.
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

    public IReadOnlyList<BrowserSessionConnection> Snapshot()
    {
        var result = new List<BrowserSessionConnection>();
        foreach (var (sessionId, connections) in _connections)
        {
            var http = connections.Values.FirstOrDefault();
            if (http is null) continue;
            var realm = http.Items.TryGetValue(Modgud.Infrastructure.Persistence.Tenancy.TenantConstants.HttpContextTenantIdKey, out var v)
                ? v as string
                : null;
            result.Add(new BrowserSessionConnection(sessionId, realm));
        }
        return result;
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
