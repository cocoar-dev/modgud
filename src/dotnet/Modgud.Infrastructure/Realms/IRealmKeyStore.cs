using Microsoft.IdentityModel.Tokens;

namespace Modgud.Infrastructure.Realms;

/// <summary>
/// Per-realm RSA key management for OpenIddict token signing and validation.
/// Backed by Marten documents in the master DB; results are cached in-memory
/// keyed by realm slug. Cache is invalidated automatically on rotation.
///
/// <para>
/// Crypto isolation guarantee: every realm has its own RSA key pair, so a
/// token signed for realm A is cryptographically unable to validate against
/// realm B's JWKS. Rotating one realm's key has zero blast radius on others.
/// </para>
/// </summary>
/// <summary>FK-03 — a scheduled rotation: the published key and when it starts signing.</summary>
public sealed record ScheduledSigningKey(string KeyId, DateTimeOffset ActivatesAt);

public interface IRealmKeyStore
{
    /// <summary>
    /// FK-03 — the planned rotation. Creates the next key and publishes it at once (JWKS,
    /// verification set) but lets it sign only after <c>RealmKeyStore.PrePublicationDelay</c>,
    /// so peer instances and external resource servers know it before the first token
    /// carries its kid. Then the previous key retires into the normal overlap. A rotation
    /// already pending is returned as is.
    /// </summary>
    Task<ScheduledSigningKey> ScheduleRotationAsync(string realmSlug, CancellationToken ct = default);

    /// <summary>
    /// Active <see cref="SigningCredentials"/> the OpenIddict server uses
    /// to sign newly issued tokens for the given realm. Generates and
    /// persists a fresh RSA-2048 key on first call for a realm that has
    /// none yet — first realm-bound /connect/token request bootstraps
    /// the key transparently.
    /// </summary>
    Task<SigningCredentials> GetActiveSigningCredentialsAsync(
        string realmSlug, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="SecurityKey"/> usable to verify a token
    /// claimed to be from this realm — the active key plus any retired
    /// keys still inside the rotation overlap window. Only this realm's
    /// keys are returned; cross-realm tokens fail signature validation.
    /// </summary>
    Task<IReadOnlyList<SecurityKey>> GetVerificationKeysAsync(
        string realmSlug, CancellationToken ct = default);

    /// <summary>
    /// Rotates the realm's signing key: generates a new RSA-2048 keypair,
    /// marks the previous active key as retired (kept for the overlap
    /// window so in-flight tokens stay validatable), and returns the new
    /// active credentials. Manual operator action — there is no scheduled
    /// auto-rotation; it is triggered via the admin endpoint
    /// (<c>POST /api/admin/realm-settings/rotate-signing-key</c>) or the
    /// recovery CLI (<c>recover rotate-signing-key --realm</c>).
    /// </summary>
    Task<SigningCredentials> RotateAsync(
        string realmSlug, CancellationToken ct = default);

    /// <summary>
    /// FK-01 — rotates like <see cref="RotateAsync(string, CancellationToken)"/>; with
    /// <paramref name="revokePrevious"/> the key(s) active until now are revoked instead of
    /// retired into the overlap window, so they verify nothing from this moment on. The
    /// response to a suspected compromise of the active key.
    /// </summary>
    Task<SigningCredentials> RotateAsync(
        string realmSlug, bool revokePrevious, CancellationToken ct = default);

    /// <summary>
    /// FK-01 — revokes every retired key still inside its overlap window (for a compromise
    /// discovered after an ordinary rotation). The active key is untouched. Returns the number
    /// of keys revoked.
    /// </summary>
    Task<int> RevokeRetiredKeysAsync(string realmSlug, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes retired keys whose rotation overlap window has elapsed
    /// (<c>RetiredAt + RotationOverlap &lt; now</c>) for the given realm, and
    /// drops the cached verification set so it rebuilds without them. Active
    /// and still-in-overlap keys are left untouched. Returns the number of
    /// keys purged. Idempotent — a no-op when nothing is due. Driven per-realm
    /// by the background <c>SigningKeyJanitorJob</c>.
    /// </summary>
    Task<int> PurgeExpiredRetiredKeysAsync(
        string realmSlug, CancellationToken ct = default);
}
