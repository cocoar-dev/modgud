using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.Inbox;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Infrastructure.FollowUps;
using Wolverine;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// B3-EI-04 — tell the admins that a change request awaits their review. Written in the commit
/// that moved the request to <see cref="ChangeRequestStatus.AdminApprovalPending"/>.
/// </summary>
public sealed record NotifyAdminsOfChangeRequest(Guid RequestId) : IFollowUpMessage;

/// <summary>
/// Creates the admins' "needs review" items for a request that is still pending, and records
/// their ids on it with a patch (never a whole-document store, which could write back a status
/// an approval changed meanwhile). An approval, rejection or withdrawal that committed while the
/// items were created is caught by the second status check: the handler dismisses its own items.
/// A repeat replaces the items (the kind dedups by source).
/// </summary>
public static class NotifyAdminsOfChangeRequestHandler
{
    public static async Task Handle(NotifyAdminsOfChangeRequest message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var realm = FollowUpRealm.Enter(envelope, $"Admin notification for change request '{message.RequestId}'");
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var session = services.GetRequiredService<IDocumentSession>();

        var request = await session.LoadAsync<UserChangeRequest>(message.RequestId, ct);
        if (request?.Status != ChangeRequestStatus.AdminApprovalPending) return;
        var user = await session.LoadAsync<ApplicationUser>(request.UserId, ct);
        if (user is null) return;

        var inbox = services.GetRequiredService<IInboxNotifier>();
        var ids = await ProfileEndpoints.NotifyAdminsInboxAsync(
            services.GetRequiredService<IAdminNotifier>(), inbox, request, user, ct);
        if (ids.Count == 0) return;

        session.Patch<UserChangeRequest>(request.Id).Set(r => r.AdminInboxItemIds, ids.ToList());
        await session.SaveChangesAsync(ct);

        var now = await session.LoadAsync<UserChangeRequest>(request.Id, ct);
        if (now?.Status != ChangeRequestStatus.AdminApprovalPending)
            await inbox.DismissByIdsAsync(ids, ct);
    }
}
