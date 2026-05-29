using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// Default <see cref="IUserAccessRevoker"/>. Each step runs against its own
/// store/session (OpenIddict managers + the session service + the Identity
/// stores), so this is a best-effort, commit-as-you-go security operation — it
/// does not participate in the caller's Marten transaction. Callers invoke it
/// before they stage the user's soft-delete (see the interface remarks).
/// </summary>
public sealed class UserAccessRevoker(
    UserManager<ApplicationUser> userManager,
    ISessionService sessionService,
    IOAuthGrantRevoker grantRevoker,
    ILogger<UserAccessRevoker> logger) : IUserAccessRevoker
{
    public async Task RevokeAllAccessAsync(Guid userId, AccessRevocationReason reason, CancellationToken ct = default)
    {
        var subject = userId.ToString();

        // 1) OAuth tokens — immediate kill for reference tokens (server default).
        //    Clients opting into JWT access tokens keep their already-issued
        //    access token until expiry (see IOAuthGrantRevoker remarks); refresh
        //    + new-token paths are still cut off. Authorizations (consent grants)
        //    only on permanent removal: a reversible deactivation/force-logout
        //    keeps consent so the user isn't dragged back through the consent
        //    screen on return.
        var tokens = await grantRevoker.RevokeTokensBySubjectAsync(subject, ct);
        var authorizations = 0;
        if (reason == AccessRevocationReason.Deletion)
            authorizations = await grantRevoker.RevokeAuthorizationsBySubjectAsync(subject, ct);

        // 2) Device-session rows (clean device list / GDPR scrub).
        await sessionService.RevokeAllSessionsAsync(userId, exceptSessionId: null, ct);

        // 3) Rotate the security stamp → existing auth cookies fail at the next
        //    SecurityStampValidator pass (<=5 min) and refresh grants fail the
        //    OAUTH-07 parity check. The user doc may be unloadable (already
        //    soft-deleted, or a Person without an ApplicationUser row) — in that
        //    case the cookie half of the kill switch can't fire; log it so the
        //    degradation is observable rather than silent.
        var user = await userManager.FindByIdAsync(subject);
        var stampRotated = user is not null;
        if (stampRotated)
            await userManager.UpdateSecurityStampAsync(user!);
        else
            logger.LogWarning(
                "Auth: security-stamp rotation skipped for user {UserId} (reason={Reason}) — no loadable ApplicationUser; existing auth cookies are not force-expired by this revoke (tokens + sessions were still revoked)",
                userId, reason);

        logger.LogInformation(
            "Auth: revoked access for user {UserId} (reason={Reason}, tokens={TokenCount}, authorizations={AuthorizationCount}, stampRotated={StampRotated})",
            userId, reason, tokens, authorizations, stampRotated);
    }
}
