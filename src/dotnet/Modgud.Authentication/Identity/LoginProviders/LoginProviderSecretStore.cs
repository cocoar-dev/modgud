using Microsoft.AspNetCore.DataProtection;

namespace Modgud.Authentication.Identity.LoginProviders;

/// <summary>
/// Encrypts/decrypts external-IdP client secrets at rest via ASP.NET
/// <c>DataProtection</c>. Keys are machine-rooted by default in Development
/// and persisted by the host in Production (see <c>services.AddDataProtection()</c>
/// docs — rotate keys by backing store config, not by this class).
/// <para>
/// Event payloads only carry encrypted bytes — never plaintext, never base64
/// of plaintext. This class is the ONLY place in the codebase that holds the
/// plaintext client secret in memory, and only for the duration of a single
/// decrypt operation.
/// </para>
/// </summary>
public class LoginProviderSecretStore
{
    // Purpose string is kept stable from the legacy IdpSecretStore so existing
    // encrypted bytes remain decryptable across the rename.
    private const string Purpose = "Modgud.IdpConfig.ClientSecret.v1";
    private readonly IDataProtector _protector;

    public LoginProviderSecretStore(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public byte[] Encrypt(string plaintext)
        => _protector.Protect(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Decrypt(byte[] encrypted)
        => System.Text.Encoding.UTF8.GetString(_protector.Unprotect(encrypted));

    public string? TryDecrypt(byte[]? encrypted)
    {
        if (encrypted is null || encrypted.Length == 0) return null;
        try { return Decrypt(encrypted); }
        catch { return null; }
    }
}
