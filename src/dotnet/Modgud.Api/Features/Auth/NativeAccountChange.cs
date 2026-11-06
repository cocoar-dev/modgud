using System.Globalization;
using Marten;
using Modgud.Authentication.Api.Account;
using Modgud.Authentication.Domain;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// RV-0001 stage 1 — what the native passkey endpoints do when they change an account's factors,
/// without changing what they allow. The browser asks for a recent proof (15 min,
/// <see cref="AccountChangeGuard.Window"/>) and mails a notice; the native endpoints did neither.
/// They now mail the same notice, and log a warning where the browser would have asked for a
/// fresh sign-in: the account already has a strong factor and the token's <c>auth_time</c> (the
/// latest proven factor) is older than the window. Stage 2 — refusing with RFC 9470's
/// <c>insufficient_user_authentication</c> — is a separate decision; the warnings show whether an
/// existing native app would be affected.
/// </summary>
internal static class NativeAccountChange
{
    /// <summary>TOTP or any passkey. The browser narrows passkeys to the page's RP ID; a native
    /// call has no page, so every passkey counts here. Read BEFORE the change.</summary>
    public static async Task<bool> HasStrongFactorAsync(IDocumentSession session, ApplicationUser user, CancellationToken ct) =>
        user.TwoFactorEnabled
        || await session.Query<StoredPasskeyCredential>().AnyAsync(c => c.UserId == user.Id, ct);

    public static async Task RecordAsync(
        HttpContext http, IAccountChangeGuard accountChanges, ILogger logger, ApplicationUser user,
        string? clientId, AccountChange change, bool hadStrongFactor, CancellationToken ct)
    {
        if (hadStrongFactor && !IsRecent(http))
        {
            logger.LogWarning(
                "Native {Change} for user {UserId} by client {ClientId} without a recent sign-in (auth_time {AuthTime}); " +
                "the browser would ask to sign in again first. RV-0001 stage 1: logged, not refused.",
                change, user.Id, clientId ?? "unknown", AuthTime(http)?.ToString("O", CultureInfo.InvariantCulture) ?? "none");
        }
        await accountChanges.NotifyAsync(user, change, ct);
    }

    private static bool IsRecent(HttpContext http) =>
        AuthTime(http) is { } at && at >= DateTimeOffset.UtcNow - AccountChangeGuard.Window;

    private static DateTimeOffset? AuthTime(HttpContext http) =>
        long.TryParse(http.User.FindFirst(Claims.AuthenticationTime)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
