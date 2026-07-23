using ErrorOr;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.Sessions;

public sealed record CreateClientSessionRequest(
    Guid UserId,
    string ClientId,
    string OAuthApplicationId,
    string AuthorizationId,
    string? ClientDisplayName,
    string? IpAddress,
    string? UserAgent);

public interface IClientSessionService
{
    Task<ClientSessionPolicy> ResolvePolicyAsync(string clientId, CancellationToken ct = default);
    Task<ClientSession> CreateAsync(CreateClientSessionRequest request, CancellationToken ct = default);
    Task<ClientSession?> ValidateAndTouchAsync(
        Guid userId,
        Guid clientSessionId,
        string clientId,
        string? authorizationId,
        CancellationToken ct = default);
    Task<IReadOnlyList<ClientSessionDto>> GetSessionsAsync(Guid userId, CancellationToken ct = default);
    Task<ErrorOr<bool>> RevokeAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task RevokeAllAsync(Guid userId, bool revokeGrants, CancellationToken ct = default);
    Task<int> PruneExpiredAsync(CancellationToken ct = default);
}
