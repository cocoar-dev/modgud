using System.Security.Cryptography;
using System.Text;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Helper for PKCE (Proof Key for Code Exchange) operations.
/// </summary>
public static class PkceHelper
{
    /// <summary>
    /// Generates a cryptographically random code verifier (43-128 characters).
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Computes the S256 code challenge from a code verifier.
    /// </summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Generates a cryptographically random nonce.
    /// </summary>
    public static string GenerateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Generates a cryptographically random state parameter.
    /// </summary>
    public static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
