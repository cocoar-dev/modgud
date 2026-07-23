using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Modgud.Authentication.Sessions;

namespace Modgud.Api.Realtime;

/// <summary>
/// Binds every authenticated SignalR connection to the browser-session claim.
/// Targeted revocation aborts the upgraded connection immediately; each hub
/// invocation also re-checks the authoritative row.
/// </summary>
public sealed class BrowserSessionHubFilter(
    IBrowserSessionConnectionRegistry connections) : IHubFilter
{
    private readonly ConcurrentDictionary<string, IDisposable> _registrations = new();

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        var http = context.Context.GetHttpContext();
        var raw = context.Context.User?.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
        if (http is null || !Guid.TryParse(raw, out var sessionId))
        {
            context.Context.Abort();
            return;
        }

        var registration = connections.Register(
            sessionId, context.Context.ConnectionId, http);
        if (_registrations.TryGetValue(context.Context.ConnectionId, out var previous))
            previous.Dispose();
        _registrations[context.Context.ConnectionId] = registration;

        try
        {
            await next(context);
        }
        catch
        {
            RemoveRegistration(context.Context.ConnectionId);
            throw;
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        RemoveRegistration(context.Context.ConnectionId);
        await next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var http = invocationContext.Context.GetHttpContext();
        var userId = http?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var rawSessionId = http?.User.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
        if (http is null ||
            !Guid.TryParse(userId, out var parsedUserId) ||
            !Guid.TryParse(rawSessionId, out var sessionId))
        {
            invocationContext.Context.Abort();
            throw new HubException("The browser session is no longer valid.");
        }

        await using var scope = http.RequestServices.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        if (await sessions.ValidateSessionAsync(
                parsedUserId, sessionId, touch: true, http.RequestAborted) is null)
        {
            invocationContext.Context.Abort();
            throw new HubException("The browser session is no longer valid.");
        }

        return await next(invocationContext);
    }

    private void RemoveRegistration(string connectionId)
    {
        if (_registrations.TryRemove(connectionId, out var registration))
            registration.Dispose();
    }
}
