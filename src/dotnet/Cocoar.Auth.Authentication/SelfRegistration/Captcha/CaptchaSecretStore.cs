using Microsoft.AspNetCore.DataProtection;

namespace Cocoar.Auth.Authentication.SelfRegistration.Captcha;

/// <summary>
/// Encrypts/decrypts per-realm Cloudflare-Turnstile captcha-secrets at
/// rest via ASP.NET <c>DataProtection</c>. Mirrors the
/// <c>LoginProviderSecretStore</c> pattern: same DataProtection-key
/// infrastructure, dedicated purpose-string so the two ciphertexts can't
/// be reused across each other.
///
/// <para>Why a separate Purpose: data-protection-key rotation should be
/// surgical — if we ever need to rotate captcha secrets specifically
/// (e.g. on a Cloudflare-side breach), the IdP-client-secret blobs stay
/// readable.</para>
///
/// <para>Captcha secrets are NOT hashable like passwords: we have to
/// send the plaintext to <c>challenges.cloudflare.com/turnstile/v0/siteverify</c>
/// on every verify call. Symmetric encryption with a host-rooted key is
/// the standard pattern — same trade-off as OAuth-client-secrets,
/// LoginProvider-client-secrets, SMTP passwords, etc.</para>
/// </summary>
public sealed class CaptchaSecretStore
{
    private const string Purpose = "Cocoar.Auth.SelfRegistration.CaptchaSecret.v1";
    private readonly IDataProtector _protector;

    public CaptchaSecretStore(IDataProtectionProvider provider)
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
