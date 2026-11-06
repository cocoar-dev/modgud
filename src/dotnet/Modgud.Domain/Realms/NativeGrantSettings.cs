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
    /// Refresh-token lifetime for native-grant tokens — <b>not applied</b> (review EG-08,
    /// 2026-11-06). The native refresh token gets the client-session idle lifetime
    /// (<c>ClientSessionPolicy.IdleLifetime</c>, default 30 days), at issuance and on every
    /// rotation; this value is validated and stored, but nothing reads it at token time.
    /// Applying it now would shorten existing native sessions without notice, so it stays inert
    /// until it is removed or wired on purpose. The refresh token is always a revocable reference
    /// token; revoking it plus a security-stamp rotation is the kill-switch.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(14);
}
