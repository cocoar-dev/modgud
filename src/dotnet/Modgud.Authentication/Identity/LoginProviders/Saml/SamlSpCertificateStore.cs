using Microsoft.AspNetCore.DataProtection;

namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Encrypts/decrypts the SAML SP certificate PFX (PKCS#12) blob at rest via
/// ASP.NET <c>DataProtection</c>. Counterpart to <c>LoginProviderSecretStore</c>
/// for the OIDC side; separate purpose string keeps the two key namespaces
/// independent so an SP-cert leak doesn't compromise OIDC client secrets and
/// vice versa.
/// <para>
/// Plaintext PFX bytes are never stored on disk and never serialised onto an
/// event payload. The only place plaintext exists is the brief in-memory
/// window where the calling service holds the decrypted bytes to load an
/// <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>.
/// </para>
/// </summary>
public class SamlSpCertificateStore
{
    private const string Purpose = "Modgud.Saml.SpCertificate.v1";
    private readonly IDataProtector _protector;

    public SamlSpCertificateStore(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>Encrypts the PFX bytes for at-rest storage.</summary>
    public byte[] Encrypt(byte[] pfxBytes) => _protector.Protect(pfxBytes);

    /// <summary>Decrypts a previously-encrypted PFX blob. Throws on tamper / wrong key.</summary>
    public byte[] Decrypt(byte[] encrypted) => _protector.Unprotect(encrypted);

    /// <summary>Best-effort decrypt — returns null instead of throwing for empty / corrupted input.</summary>
    public byte[]? TryDecrypt(byte[]? encrypted)
    {
        if (encrypted is null || encrypted.Length == 0) return null;
        try { return Decrypt(encrypted); }
        catch { return null; }
    }
}
