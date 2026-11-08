using System.Globalization;
using Modgud.Authentication.Api.Account;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// RV-0001 — what the native passkey endpoints do when they change an account's factors. The
/// browser asks for a recent proof (15 min, <see cref="AccountChangeGuard.Window"/>) and mails a
/// notice. The native endpoints mail the same notice (stage 1) and, with
/// <see cref="NativeGrantSettings.RequireRecentSignInForPasskeyChanges"/> on, refuse a change
/// whose token's <c>auth_time</c> (the latest proven factor) is older than the window, with
/// RFC 9470's <c>insufficient_user_authentication</c> (stage 2). Any sign-in the app allows
/// counts, an e-mail code or magic link included: the mailbox is the binding way back for a
/// user whose passkey device is gone, while a stolen refresh token alone no longer suffices.
/// Switched off, the change goes ahead and a warning is logged where it would be refused.
/// </summary>
internal static class NativeAccountChange
{
    /// <summary>RFC 9470 <c>max_age</c>: the window in seconds.</summary>
    private static readonly int MaxAgeSeconds = (int)AccountChangeGuard.Window.TotalSeconds;

    public static bool IsRecent(DateTimeOffset? authTime, DateTimeOffset now) =>
        authTime is { } at && at >= now - AccountChangeGuard.Window;

    /// <summary>
    /// Null when the change may go ahead; otherwise the RFC 9470 refusal to return. A token
    /// without <c>auth_time</c> counts as stale. <paramref name="log"/> off for a step that only
    /// prepares the change (the enrollment's begin), so one change logs once.
    /// </summary>
    public static IResult? CheckRecentSignIn(
        HttpContext http, NativeGrantSettings settings, ILogger logger, ApplicationUser user,
        string? clientId, AccountChange change, bool log)
    {
        var authTime = AuthTime(http);
        if (IsRecent(authTime, DateTimeOffset.UtcNow)) return null;

        var at = authTime?.ToString("O", CultureInfo.InvariantCulture) ?? "none";
        if (settings.RequireRecentSignInForPasskeyChanges)
        {
            if (log)
                logger.LogInformation(
                    "Native {Change} for user {UserId} by client {ClientId} refused: no sign-in within the last {Minutes} minutes (auth_time {AuthTime}).",
                    change, user.Id, clientId ?? "unknown", AccountChangeGuard.Window.TotalMinutes, at);
            return new InsufficientUserAuthentication(MaxAgeSeconds);
        }

        if (log)
            logger.LogWarning(
                "Native {Change} for user {UserId} by client {ClientId} without a recent sign-in (auth_time {AuthTime}); " +
                "it would be refused with RequireRecentSignInForPasskeyChanges on. Logged, not refused.",
                change, user.Id, clientId ?? "unknown", at);
        return null;
    }

    /// <summary>The same notice the browser mails for the change.</summary>
    public static Task NotifyAsync(IAccountChangeGuard accountChanges, ApplicationUser user, AccountChange change, CancellationToken ct) =>
        accountChanges.NotifyAsync(user, change, ct);

    private static DateTimeOffset? AuthTime(HttpContext http) =>
        long.TryParse(http.User.FindFirst(Claims.AuthenticationTime)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>RFC 9470 §3: 401 with the Bearer challenge naming the error and the acceptable
    /// age, and the same fields as JSON for clients that read the body.</summary>
    private sealed class InsufficientUserAuthentication(int maxAgeSeconds) : IResult
    {
        private const string Description = "A more recent sign-in is required to change this account's passkeys.";

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate =
                $"Bearer error=\"insufficient_user_authentication\", error_description=\"{Description}\", max_age=\"{maxAgeSeconds}\"";
            return httpContext.Response.WriteAsJsonAsync(new Dictionary<string, object>
            {
                ["error"] = "insufficient_user_authentication",
                ["error_description"] = Description,
                ["max_age"] = maxAgeSeconds,
            });
        }
    }
}
