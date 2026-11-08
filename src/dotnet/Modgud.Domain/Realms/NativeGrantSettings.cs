namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm configuration for the native (cookieless) passwordless token
/// grants (ADR-0010: <c>urn:cocoar:otp</c>, <c>urn:cocoar:magic</c>). Lives as a
/// sub-record on the tenant-DB <see cref="RealmSettings.RealmSettings"/>
/// aggregate alongside <see cref="DcrSettings"/> / <see cref="CimdSettings"/>,
/// owned by the realm-admin. Default-disabled: every realm starts with
/// <c>Enabled=false</c> so <c>/connect/token</c> rejects the native grants until
/// an admin opts in.
///
/// <para>The per-realm flag is the master gate; the per-client opt-in (the
/// <c>gt:urn:cocoar:*</c> OpenIddict application permission) is a separate,
/// additional gate. A realm with this enabled still only mints native-grant
/// tokens for clients that carry the matching grant-type permission.</para>
/// </summary>
public record NativeGrantSettings
{
    /// <summary>
    /// Master toggle. When <c>false</c>, the token endpoint rejects every
    /// <c>urn:cocoar:*</c> grant for this realm with
    /// <c>unsupported_grant_type</c>, and the anonymous native OTP-request
    /// endpoint returns its generic response without sending a code.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Access-token lifetime for tokens minted via a native grant. Deliberately
    /// short: a native client takes a self-contained JWT access token (per-client
    /// <c>AccessTokenType.Jwt</c>) which is not individually revocable, so a short
    /// TTL bounds the residual window after the (revocable, reference) refresh
    /// token is revoked / the security stamp is rotated. Default 15 minutes.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// RV-0001 stage 2 — adding or removing a passkey through the native endpoints needs a
    /// sign-in from the last 15 minutes (the token's <c>auth_time</c>), as the browser asks for
    /// one. Otherwise the call is refused with RFC 9470's <c>insufficient_user_authentication</c>
    /// and <c>max_age</c>, and the app signs the user in again. Any sign-in the app allows
    /// counts, an e-mail code or magic link included — the way back for a user whose passkey
    /// device is gone. Off by default (an app must handle the refusal first): then the change
    /// goes ahead and a warning is logged where it would have been refused.
    /// </summary>
    public bool RequireRecentSignInForPasskeyChanges { get; init; }

    // No refresh-token lifetime here: a native refresh token gets the client-session idle
    // lifetime (ClientSessionPolicy.IdleLifetime, default 30 days) at issuance and on every
    // rotation. The former RefreshTokenLifetime was stored and validated but never applied
    // (review EG-08) and is gone; stored documents that still carry it are read without it.
    // The refresh token is always a revocable reference token; revoking it plus a
    // security-stamp rotation is the kill-switch.
}
