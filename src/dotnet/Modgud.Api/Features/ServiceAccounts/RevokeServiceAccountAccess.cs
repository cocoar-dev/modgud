using Marten;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.OpenIddict;
using Wolverine;

namespace Modgud.Api.Features.ServiceAccounts;

/// <summary>
/// B7-SC05 — the revoke owed by deactivating or deleting a service account: its M2M tokens carry
/// sub = the account id, so a by-subject revoke ends them all. Written as a safety net in the commit
/// of the change; the endpoint still revokes at once.
/// </summary>
public sealed record RevokeServiceAccountAccess(Guid ServiceAccountId) : IFollowUpMessage;

/// <summary>
/// Runs once the safety-net delay has passed, so never next to the endpoint's own revoke. An
/// account that is active again by then keeps its tokens: they were issued after the
/// reactivation. Both revokes are idempotent; a failure is retried and finally dead-lettered.
/// </summary>
public static class RevokeServiceAccountAccessHandler
{
    public static async Task Handle(
        RevokeServiceAccountAccess message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        await FollowUpTiming.WaitUntilDueAsync(envelope, ct);
        using var realm = FollowUpRealm.Enter(envelope, $"Access revoke for service account '{message.ServiceAccountId}'");
        await using var scope = scopes.CreateAsyncScope();

        var account = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<ServiceAccount>(message.ServiceAccountId, ct);
        if (account is { IsActive: true, IsDeleted: false }) return;

        var revoker = scope.ServiceProvider.GetRequiredService<IOAuthGrantRevoker>();
        var subject = message.ServiceAccountId.ToString();
        await revoker.RevokeTokensBySubjectAsync(subject, ct);
        await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);
    }
}
