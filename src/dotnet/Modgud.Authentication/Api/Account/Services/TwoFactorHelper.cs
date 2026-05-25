using Marten;
using Modgud.Authentication.Domain;

namespace Modgud.Authentication.Api.Account.Services;

/// <summary>
/// Shared helper to determine which 2FA methods a user has configured.
/// Used by /me endpoint, login enforcement, and disable-protection guards.
/// </summary>
public static class TwoFactorHelper
{
    public static async Task<List<string>> GetMethodsAsync(ApplicationUser user, IQuerySession session)
    {
        var passkeyCount = await session.Query<StoredPasskeyCredential>()
            .Where(c => c.UserId == user.Id)
            .CountAsync();
        return BuildMethodsList(user, passkeyCount);
    }

    /// <summary>
    /// Pure list-builder: which 2FA methods does this user count as configured,
    /// given the number of stored passkey credentials? Order matters — the SPA
    /// renders methods in the order returned (TOTP first, then email, then
    /// passkey). The email branch additionally requires a non-empty
    /// <see cref="ApplicationUser.Email"/> because a flag without a destination
    /// address is not a usable factor.
    /// </summary>
    internal static List<string> BuildMethodsList(ApplicationUser user, int passkeyCount)
    {
        var methods = new List<string>();
        if (user.TwoFactorEnabled) methods.Add("totp");
        if (user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email)) methods.Add("email");
        if (passkeyCount > 0) methods.Add("passkey");
        return methods;
    }

    /// <summary>
    /// Forces the SecureSetupModal to be blocking on the user's next request — same as
    /// the admin "Force immediate enforcement" action. Used after a user removes their
    /// last 2FA method while AuthenticationMinimumLevel ≥ 1: instead of refusing the
    /// removal, we let it through but expire the grace so the next login lands on the
    /// blocking setup modal without a fresh 14-day window.
    ///
    /// Returns <c>false</c> (no-op) when the user is <c>TwoFactorExempt</c> — exempt
    /// users bypass enforcement anyway, so stamping a past DueAt would have no effect
    /// and just confuses the audit trail.
    /// </summary>
    public static async Task<bool> ExpireSetupGraceAsync(Guid userId, IDocumentSession session)
    {
        var security = await session.LoadAsync<UserSecurityData>(userId)
            ?? UserSecurityData.Create(userId);
        if (!TryExpireSetupGrace(security, DateTime.UtcNow)) return false;
        session.Store(security);
        return true;
    }

    /// <summary>
    /// Pure mutation: stamps <see cref="UserSecurityData.SecureSetupDueAt"/> to
    /// <paramref name="now"/> unless the user is <see cref="UserSecurityData.TwoFactorExempt"/>.
    /// Returns <c>true</c> when the record was changed (caller must persist),
    /// <c>false</c> when the call was a deliberate no-op for exempt users.
    /// </summary>
    internal static bool TryExpireSetupGrace(UserSecurityData security, DateTime now)
    {
        if (security.TwoFactorExempt) return false;
        security.SecureSetupDueAt = now;
        return true;
    }
}
