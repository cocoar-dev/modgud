using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// Service for managing user sessions.
/// </summary>
public class SessionService : ISessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IDeviceInfoService _deviceInfoService;
    private static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromDays(14);

    public SessionService(ISessionRepository sessionRepository, IDeviceInfoService deviceInfoService)
    {
        _sessionRepository = sessionRepository;
        _deviceInfoService = deviceInfoService;
    }

    public async Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var sessions = await _sessionRepository.GetByUserIdAsync(userId, cancellationToken);

        var sessionDtos = sessions.Select(s => new SessionDto
        {
            Id = s.Id.ToString(),
            IpAddress = s.IpAddress,
            Browser = s.Browser,
            BrowserVersion = s.BrowserVersion,
            OperatingSystem = s.OperatingSystem,
            OsVersion = s.OsVersion,
            DeviceType = s.DeviceType,
            CreatedAt = s.CreatedAt,
            LastActiveAt = s.LastActiveAt,
            IsCurrent = currentSessionId.HasValue && s.Id == currentSessionId.Value
        }).ToList();

        return new SessionListDto { Sessions = sessionDtos };
    }

    public async Task<ErrorOr<UserSession>> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        var deviceInfo = _deviceInfoService.Parse(userAgent);

        var session = UserSession.Create(
            userId,
            sessionId: Guid.NewGuid().ToString(), // Will be correlated with auth cookie
            ipAddress,
            userAgent,
            deviceInfo.Browser,
            deviceInfo.BrowserVersion,
            deviceInfo.OperatingSystem,
            deviceInfo.OsVersion,
            deviceInfo.DeviceType,
            DefaultSessionDuration);

        await _sessionRepository.CreateAsync(session, cancellationToken);

        return session;
    }

    public async Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionRepository.GetByIdAsync(sessionId, cancellationToken);

        if (session is null)
        {
            return SessionErrors.NotFound(sessionId);
        }

        if (session.UserId != userId)
        {
            return SessionErrors.NotOwner;
        }

        await _sessionRepository.DeleteAsync(sessionId, cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken cancellationToken = default)
    {
        if (exceptSessionId.HasValue)
        {
            await _sessionRepository.DeleteAllExceptAsync(userId, exceptSessionId.Value, cancellationToken);
        }
        else
        {
            await _sessionRepository.DeleteAllForUserAsync(userId, cancellationToken);
        }

        return true;
    }

    public async Task TouchSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is not null)
        {
            session.Touch();
            await _sessionRepository.UpdateAsync(session, cancellationToken);
        }
    }
}
