namespace Modgud.Authentication.Domain.Saml;

/// <summary>
/// Per-realm SAML SP certificate state. One document per tenant DB, addressed
/// by the fixed <see cref="SingletonId"/>. The plaintext PFX bytes are never
/// stored — <see cref="ActiveCertPfxEncrypted"/> and
/// <see cref="PreviousCertPfxEncrypted"/> hold DataProtection-protected blobs;
/// only <c>Modgud.Authentication.Identity.LoginProviders.Saml.SamlSpCertificateStore</c>
/// can decrypt them.
/// <para>
/// Two slots support cert rotation with a metadata-advertised overlap window:
/// after a rotate, the previous cert remains in <see cref="PreviousCertPfxEncrypted"/>
/// and is still advertised in our SP metadata until <see cref="PreviousRetiresAt"/>.
/// IdPs that signed assertions to the old key keep working until they refresh
/// metadata and start using the new key.
/// </para>
/// </summary>
public class SamlSpCertificateDocument
{
    /// <summary>Sentinel Guid for the singleton row per tenant DB.</summary>
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-00000000A4ED");

    public Guid Id { get; set; } = SingletonId;

    /// <summary>
    /// PFX (PKCS#12) bytes of the active SP certificate, encrypted via
    /// DataProtection. Decrypt only through <c>SamlSpCertificateStore</c>.
    /// </summary>
    public byte[] ActiveCertPfxEncrypted { get; set; } = [];

    /// <summary>SHA-1 thumbprint of the active cert (plaintext, for admin display + log correlation).</summary>
    public string ActiveCertThumbprint { get; set; } = string.Empty;

    /// <summary>NotBefore of the active cert (plaintext, so we know validity without decrypting).</summary>
    public DateTimeOffset ActiveCertNotBefore { get; set; }

    /// <summary>NotAfter of the active cert (plaintext).</summary>
    public DateTimeOffset ActiveCertNotAfter { get; set; }

    /// <summary>When this document's active cert was generated.</summary>
    public DateTimeOffset ActiveCertCreatedAt { get; set; }

    /// <summary>
    /// Previous cert PFX (DataProtection-encrypted). Populated during the
    /// rotation overlap window so SP metadata can advertise both. Cleared by
    /// the retire step once <see cref="PreviousRetiresAt"/> has passed.
    /// </summary>
    public byte[]? PreviousCertPfxEncrypted { get; set; }

    /// <summary>SHA-1 thumbprint of the previous cert (plaintext, for admin display).</summary>
    public string? PreviousCertThumbprint { get; set; }

    /// <summary>NotAfter of the previous cert.</summary>
    public DateTimeOffset? PreviousCertNotAfter { get; set; }

    /// <summary>
    /// When the previous cert should be dropped from <see cref="PreviousCertPfxEncrypted"/>
    /// + SP metadata. <c>null</c> when there is no previous cert.
    /// </summary>
    public DateTimeOffset? PreviousRetiresAt { get; set; }
}
