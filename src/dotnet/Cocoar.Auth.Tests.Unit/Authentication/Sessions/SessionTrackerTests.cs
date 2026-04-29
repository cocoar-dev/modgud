using System.Net;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Sessions;
using ErrorOr;
using Microsoft.AspNetCore.Http;

namespace Cocoar.Auth.Tests.Unit.Authentication.Sessions;

/// <summary>
/// Pins <see cref="SessionTracker.RecordLoginAsync"/>: pulls IP + UA out of the
/// <see cref="HttpContext"/>, hands them to the session service, and swallows
/// failures (a tracking blip must NEVER bring down a login).
/// </summary>
public class SessionTrackerTests
{
    private sealed class CapturingSessionService : ISessionService
    {
        public Guid? CapturedUserId { get; private set; }
        public string? CapturedIp { get; private set; }
        public string? CapturedUa { get; private set; }
        public int CallCount { get; private set; }
        public Func<Task<ErrorOr<UserSession>>>? Behaviour { get; set; }

        public Task<ErrorOr<UserSession>> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct = default)
        {
            CallCount++;
            CapturedUserId = userId;
            CapturedIp = ipAddress;
            CapturedUa = userAgent;
            return Behaviour?.Invoke() ?? Task.FromResult<ErrorOr<UserSession>>(new UserSession { Id = Guid.NewGuid() });
        }

        public Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task TouchSessionAsync(Guid sessionId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Forwards_user_id_ip_and_user_agent_to_session_service()
    {
        var svc = new CapturingSessionService();
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        ctx.Request.Headers.UserAgent = "TestAgent/1.0";
        var userId = Guid.NewGuid();

        await SessionTracker.RecordLoginAsync(svc, ctx, userId);

        Assert.Equal(1, svc.CallCount);
        Assert.Equal(userId, svc.CapturedUserId);
        Assert.Equal("10.0.0.1", svc.CapturedIp);
        Assert.Equal("TestAgent/1.0", svc.CapturedUa);
    }

    [Fact]
    public async Task Forwards_null_ip_when_remote_address_missing()
    {
        var svc = new CapturingSessionService();
        var ctx = new DefaultHttpContext();
        // Connection.RemoteIpAddress not set → null.toString() → null
        ctx.Request.Headers.UserAgent = "ua";

        await SessionTracker.RecordLoginAsync(svc, ctx, Guid.NewGuid());

        Assert.Null(svc.CapturedIp);
        Assert.Equal("ua", svc.CapturedUa);
    }

    [Fact]
    public async Task Forwards_empty_user_agent_when_header_missing()
    {
        var svc = new CapturingSessionService();
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        await SessionTracker.RecordLoginAsync(svc, ctx, Guid.NewGuid());

        // StringValues.ToString() of a missing header is "" — pin so a future change
        // to "null when missing" surfaces here.
        Assert.Equal(string.Empty, svc.CapturedUa);
    }

    [Fact]
    public async Task Swallows_exceptions_thrown_by_session_service()
    {
        var svc = new CapturingSessionService
        {
            Behaviour = () => throw new InvalidOperationException("Marten down"),
        };
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        // Must NOT throw — login is the caller, and a tracking failure must not
        // reach the user.
        await SessionTracker.RecordLoginAsync(svc, ctx, Guid.NewGuid());

        Assert.Equal(1, svc.CallCount);
    }

    [Fact]
    public async Task Forwards_cancellation_token_through()
    {
        // The token isn't captured by the fake but we still exercise the path —
        // make sure passing one doesn't trip up the helper.
        var svc = new CapturingSessionService();
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        using var cts = new CancellationTokenSource();

        await SessionTracker.RecordLoginAsync(svc, ctx, Guid.NewGuid(), cts.Token);

        Assert.Equal(1, svc.CallCount);
    }
}
