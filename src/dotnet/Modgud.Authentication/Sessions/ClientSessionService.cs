using System.Globalization;
using ErrorOr;
using Marten;
using JasperFx;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Authentication.Sessions;

public sealed class ClientSessionService(
    IDocumentSession session,
    IDeviceInfoService deviceInfo,
    IRealmSettingsService realmSettings,
    IOAuthGrantRevoker grants,
    ISessionGrantService sessionGrants) : IClientSessionService, IRefreshTokenReuseObserver
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    public async Task<ClientSessionPolicy> ResolvePolicyAsync(string clientId, CancellationToken ct = default)
    {
        var realm = await realmSettings.LoadAsync(ct);
        var realmPolicy = realm.ClientSessions ?? ClientSessionPolicy.Defaults;
        var effective = realmPolicy;

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == clientId && !x.IsDeleted, ct);
        if (client is not null && client.AppIds.Count > 0)
        {
            var appPolicies = new List<ClientSessionPolicy>();
            foreach (var appId in client.AppIds.Distinct())
            {
                var app = await session.LoadAsync<ApplicationSettings>(appId, ct);
                var overrides = app?.ClientSessions;
                appPolicies.Add(overrides is null
                    ? realmPolicy
                    : realmPolicy with
                    {
                        IdleLifetime = overrides.IdleLifetime ?? realmPolicy.IdleLifetime,
                        AbsoluteLifetime = overrides.AbsoluteLifetime ?? realmPolicy.AbsoluteLifetime,
                    });
            }

            // A multi-App client inherits the strictest participating App until
            // an explicit client override removes the ambiguity.
            if (appPolicies.Count > 0)
            {
                effective = new ClientSessionPolicy
                {
                    IdleLifetime = appPolicies.Min(x => x.IdleLifetime),
                    AbsoluteLifetime = appPolicies.Min(x => x.AbsoluteLifetime),
                };
            }
        }

        if (client is not null)
        {
            effective = effective with
            {
                IdleLifetime = ReadSeconds(client.Settings, OAuthApplicationSettingKeys.ClientSessionIdleLifetime)
                    ?? ReadSeconds(client.Settings, OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime)
                    ?? effective.IdleLifetime,
                AbsoluteLifetime = ReadSeconds(client.Settings, OAuthApplicationSettingKeys.ClientSessionAbsoluteLifetime)
                    ?? effective.AbsoluteLifetime,
            };
        }

        var max = TimeSpan.FromDays(3650);
        var absolute = Clamp(effective.AbsoluteLifetime, TimeSpan.FromDays(1), max);
        var idle = Clamp(effective.IdleLifetime, TimeSpan.FromDays(1), absolute);
        return new ClientSessionPolicy { IdleLifetime = idle, AbsoluteLifetime = absolute };
    }

    public async Task<ClientSession> CreateAsync(CreateClientSessionRequest request, CancellationToken ct = default)
    {
        var policy = await ResolvePolicyAsync(request.ClientId, ct);
        var device = deviceInfo.Parse();
        var now = DateTimeOffset.UtcNow;
        var entity = new ClientSession
        {
            Id = Guid.CreateVersion7(),
            UserId = request.UserId,
            ClientId = request.ClientId,
            OAuthApplicationId = request.OAuthApplicationId,
            AuthorizationId = request.AuthorizationId,
            ClientDisplayName = request.ClientDisplayName,
            IpAddress = request.IpAddress,
            UserAgent = request.UserAgent,
            Browser = device.Browser,
            BrowserVersion = device.BrowserVersion,
            OperatingSystem = device.OperatingSystem,
            OsVersion = device.OsVersion,
            DeviceType = device.DeviceType,
            CreatedAt = now,
            LastActiveAt = now,
            AbsoluteExpiresAt = now.Add(policy.AbsoluteLifetime),
        };
        entity.Touch(now, policy.IdleLifetime);
        session.Store(entity);
        await session.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<ClientSession?> ValidateAndTouchAsync(
        Guid userId,
        Guid clientSessionId,
        string clientId,
        string? authorizationId,
        CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<ClientSession>(clientSessionId, ct);
        var now = DateTimeOffset.UtcNow;
        if (entity is null ||
            entity.UserId != userId ||
            !string.Equals(entity.ClientId, clientId, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(authorizationId) ||
            !string.Equals(entity.AuthorizationId, authorizationId, StringComparison.Ordinal))
            return null;

        if (!entity.IsActive(now))
        {
            await RevokeCoreAsync(entity, ct);
            return null;
        }

        if (entity.LastActiveAt <= now.Subtract(TouchInterval))
        {
            var policy = await ResolvePolicyAsync(clientId, ct);
            entity.Touch(now, policy.IdleLifetime);
            session.Store(entity);
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (ConcurrencyException)
            {
                return null;
            }
        }

        return entity;
    }

    public async Task<IReadOnlyList<ClientSessionDto>> GetSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await session.Query<ClientSession>()
            .Where(x => x.UserId == userId && x.ExpiresAt > now && x.AbsoluteExpiresAt > now)
            .OrderByDescending(x => x.LastActiveAt)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<bool>> RevokeAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<ClientSession>(sessionId, ct);
        if (entity is null)
            return Error.NotFound("ClientSession.NotFound", $"Client session {sessionId} not found.");
        if (entity.UserId != userId)
            return Error.Forbidden("ClientSession.NotOwner", "Caller does not own this client session.");
        await RevokeCoreAsync(entity, ct);
        return true;
    }

    public async Task RevokeAllAsync(Guid userId, bool revokeGrants, CancellationToken ct = default)
    {
        var rows = await session.Query<ClientSession>().Where(x => x.UserId == userId).ToListAsync(ct);
        if (revokeGrants)
        {
            foreach (var row in rows)
            {
                await grants.RevokeTokensByAuthorizationIdAsync(row.AuthorizationId, ct);
                await grants.RevokeAuthorizationByIdAsync(row.AuthorizationId, ct);
            }
        }

        session.DeleteWhere<ClientSession>(x => x.UserId == userId);
        // ADR 0021 — one end marker per native session (see SessionService.RevokeAllSessionsAsync).
        foreach (var row in rows)
            await sessionGrants.StageSessionEndAsync(session, row.UserId, row.Id, AccessEndReasons.Revoked, initiatingClientId: null, ct);
        await session.SaveChangesAsync(ct);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await session.Query<ClientSession>()
            .Where(x => x.ExpiresAt <= now || x.AbsoluteExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            await grants.RevokeTokensByAuthorizationIdAsync(row.AuthorizationId, ct);
            await grants.RevokeAuthorizationByIdAsync(row.AuthorizationId, ct);
            session.Delete(row);
            await sessionGrants.StageSessionEndAsync(session, row.UserId, row.Id, AccessEndReasons.Expired, initiatingClientId: null, ct);
        }
        if (rows.Count > 0)
            await session.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task OnReuseDetectedAsync(
        string? subject,
        string? clientId,
        string? authorizationId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorizationId)) return;

        var rows = await session.Query<ClientSession>()
            .Where(x => x.AuthorizationId == authorizationId)
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            session.Delete(row);
            await sessionGrants.StageSessionEndAsync(session, row.UserId, row.Id, AccessEndReasons.Revoked, initiatingClientId: null, ct);
        }
        await session.SaveChangesAsync(ct);
    }

    private async Task RevokeCoreAsync(ClientSession entity, CancellationToken ct)
    {
        await grants.RevokeTokensByAuthorizationIdAsync(entity.AuthorizationId, ct);
        await grants.RevokeAuthorizationByIdAsync(entity.AuthorizationId, ct);
        session.Delete(entity);
        await sessionGrants.StageSessionEndAsync(session, entity.UserId, entity.Id, AccessEndReasons.Revoked, initiatingClientId: null, ct);
        await session.SaveChangesAsync(ct);
    }

    private static ClientSessionDto ToDto(ClientSession x) => new()
    {
        Id = x.Id.ToString(),
        ClientId = x.ClientId,
        ClientDisplayName = x.ClientDisplayName,
        IpAddress = x.IpAddress,
        Browser = x.Browser,
        BrowserVersion = x.BrowserVersion,
        OperatingSystem = x.OperatingSystem,
        OsVersion = x.OsVersion,
        DeviceType = x.DeviceType,
        CreatedAt = x.CreatedAt,
        LastActiveAt = x.LastActiveAt,
        ExpiresAt = x.ExpiresAt,
        AbsoluteExpiresAt = x.AbsoluteExpiresAt,
    };

    private static TimeSpan? ReadSeconds(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
