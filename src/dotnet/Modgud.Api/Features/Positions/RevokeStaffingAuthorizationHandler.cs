using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.PositionTerminals;
using OpenIddict.Abstractions;
using Wolverine;

namespace Modgud.Api.Features.Positions;

/// <summary>
/// Revokes the authorization of an ended staffing session (G-B4-02). Runs on the durable
/// <c>follow-ups</c> queue with its retry schedule: a revoke that did not happen throws, so it is
/// retried and finally a dead letter that the readiness check reports. OpenIddict's stores open
/// their Marten session through the tenanted session factory, which needs an ambient realm, so
/// the handler enters the realm the outbox stamped on the envelope.
/// </summary>
public static class RevokeStaffingAuthorizationHandler
{
    public static async Task Handle(
        RevokeStaffingAuthorization message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
            throw new FollowUpPermanentException(
                $"Authorization revoke for staffing session '{message.StaffingSessionId}' arrived without a realm.");

        // A safety net of the immediate revoke: never next to it on the same authorization.
        await FollowUpTiming.WaitUntilDueAsync(envelope, ct);
        using var realm = TenantContext.Enter(envelope.TenantId);
        await using var scope = scopes.CreateAsyncScope();

        var revoker = scope.ServiceProvider.GetRequiredService<IOAuthGrantRevoker>();
        if (await revoker.RevokeAuthorizationByIdAsync(message.AuthorizationId, ct))
            return; // revoked now, or already revoked

        // false is either "no such authorization" (nothing left to revoke) or a revoke that failed.
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        if (await authorizations.FindByIdAsync(message.AuthorizationId, ct) is null)
            return;

        throw new InvalidOperationException(
            $"The authorization '{message.AuthorizationId}' of ended staffing session '{message.StaffingSessionId}' was not revoked.");
    }
}
