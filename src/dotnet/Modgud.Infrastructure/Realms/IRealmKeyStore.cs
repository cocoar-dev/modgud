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
public interface IRealmKeyStore
{
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
    /// window), and returns the new active credentials. Manual operator
    /// action — there is no scheduled auto-rotation.
    /// </summary>
    Task<SigningCredentials> RotateAsync(
        string realmSlug, CancellationToken ct = default);
}
