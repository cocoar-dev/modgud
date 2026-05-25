namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm RSA signing key used by OpenIddict to sign access / id tokens
/// issued for that realm. Stored as a Marten document in the master (global)
/// database alongside <see cref="Realm"/> — these are infrastructure /
/// crypto-metadata records, not tenant data, so they live outside the
/// per-tenant DBs.
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
///   <item><description>Each realm has exactly one <see cref="IsActive"/>=true key at any time — the active signing key.</description></item>
///   <item><description>On rotation, the previous active key flips to <see cref="IsActive"/>=false and stays in the JWKS for an overlap window so already-issued tokens remain validatable.</description></item>
///   <item><description>After <see cref="RetiredAt"/>+overlap, retired keys can be hard-deleted by a janitor process (not implemented yet).</description></item>
/// </list>
/// </summary>
public class RealmSigningKey
{
    public Guid Id { get; set; }

    /// <summary>
    /// The realm this key belongs to (matches <see cref="Realm.Slug"/>).
    /// Indexed for fast lookup by realm.
    /// </summary>
    public string RealmSlug { get; set; } = string.Empty;

    /// <summary>
    /// Stable key identifier surfaced in the JWT <c>kid</c> header and
    /// JWKS document. The pair (RealmSlug, KeyId) is unique.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Signing algorithm — currently always RS256.
    /// </summary>
    public string Algorithm { get; set; } = "RS256";

    /// <summary>
    /// PKCS#8 PEM encoding of the RSA private key. Format chosen so an
    /// operator can extract and inspect the key with standard tooling
    /// (<c>openssl</c>) when debugging. Must NEVER leave the master DB.
    /// </summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// SubjectPublicKeyInfo PEM encoding of the public key (mirrors what's
    /// surfaced in the JWKS document). Stored alongside the private key
    /// for symmetric operator inspection.
    /// </summary>
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the realm's currently-active signing key. Exactly
    /// one record per realm carries <c>true</c>; previous keys remain at
    /// <c>false</c> until the overlap window expires.
    /// </summary>
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this key was retired (i.e. <see cref="IsActive"/> flipped to false).
    /// Null while the key is active. Once retired, the key continues to appear
    /// in the JWKS document until it can be safely garbage-collected.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
