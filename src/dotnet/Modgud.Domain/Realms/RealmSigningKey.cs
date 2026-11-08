namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm RSA signing key used by OpenIddict to sign access / id tokens
/// issued for that realm. Stored as a Marten document in the realm's OWN
/// tenant database — the private signing material never leaves the per-tenant
/// DB, so a master-DB or Realm-registry compromise cannot expose another
/// realm's key.
///
/// <para>
/// Cryptographic isolation: a token signed by realm A's key cannot be
/// validated against realm B's JWKS, so a resource server pointed at
/// realm B's discovery document automatically rejects realm A's tokens.
/// Rotation of one realm's key has zero blast radius on other realms.
/// </para>
///
/// <para>
/// Lifecycle:
/// </para>
/// <list type="bullet">
///   <item><description>Each realm has one <see cref="IsActive"/>=true key — the active signing key. The key store keeps it to one (a per-realm lock in the process); no database constraint enforces it.</description></item>
///   <item><description>On rotation, the previous active key flips to <see cref="IsActive"/>=false and stays in the JWKS for an overlap window so already-issued tokens remain validatable.</description></item>
///   <item><description>After <see cref="RetiredAt"/>+overlap, retired keys are hard-deleted by the <c>SigningKeyJanitorJob</c> (via <c>IRealmKeyStore.PurgeExpiredRetiredKeysAsync</c>).</description></item>
/// </list>
/// </summary>
public class RealmSigningKey
{
    public Guid Id { get; set; }

    /// <summary>
    /// The realm this key belongs to (matches <see cref="Realm.Slug"/>).
    /// Not indexed: a tenant DB holds only its own realm's keys, so the
    /// database itself is the realm filter.
    /// </summary>
    public string RealmSlug { get; set; } = string.Empty;

    /// <summary>
    /// Stable key identifier surfaced in the JWT <c>kid</c> header and
    /// JWKS document. Derived from the public key, so every fresh key pair
    /// gets its own; no database constraint enforces uniqueness.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Signing algorithm — currently always RS256.
    /// </summary>
    public string Algorithm { get; set; } = "RS256";

    /// <summary>
    /// PKCS#8 PEM encoding of the RSA private key. Format chosen so an
    /// operator can extract and inspect the key with standard tooling
    /// (<c>openssl</c>) when debugging. Must NEVER leave the realm's tenant DB.
    /// </summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// SubjectPublicKeyInfo PEM encoding of the public key (mirrors what's
    /// surfaced in the JWKS document). Stored alongside the private key
    /// for symmetric operator inspection.
    /// </summary>
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the realm's currently-active signing key. One record
    /// per realm carries <c>true</c> (kept so by the key store, not by a
    /// constraint); previous keys remain at <c>false</c> until the overlap
    /// window expires.
    /// </summary>
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this key was retired (i.e. <see cref="IsActive"/> flipped to false).
    /// Null while the key is active. Once retired, the key continues to appear
    /// in the JWKS document until it can be safely garbage-collected.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>
    /// FK-01 — when the key was revoked (taken out of trust at once, typically after a
    /// suspected compromise). A revoked key is neither in the verification set nor in the
    /// JWKS, whatever its overlap window says, and the janitor deletes it on its next run.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// FK-03 — a scheduled key: published (JWKS + verification set) but not yet signing.
    /// Becomes the active key once this time has passed; null for every other state.
    /// </summary>
    public DateTimeOffset? ActivatesAt { get; set; }
}
