namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Observes OpenIddict's confirmed refresh-token reuse signal before the
/// stock handler revokes the associated token family.
/// </summary>
public interface IRefreshTokenReuseObserver
{
    Task OnReuseDetectedAsync(
        string? subject,
        string? clientId,
        string? authorizationId,
        CancellationToken ct);
}
