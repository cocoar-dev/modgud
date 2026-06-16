using System.Security.Cryptography;
using System.Text;

namespace Modgud.Authentication.Domain;

/// <summary>
/// Ephemeral Marten document for Magic Link login tokens.
/// One-time use, time-limited. Token is stored as SHA256 hash.
/// </summary>
public class MagicLinkChallenge
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int ExpirationMinutes = 15;
    public const int RateLimitMinutes = 2;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Hashes a raw magic-link token for storage and lookup. Shared by the web
    /// <c>/api/account/magic-link/login</c> endpoint and the native
    /// <c>urn:cocoar:magic</c> token grant (ADR-0010) so both hash identically:
    /// SHA-256 over the UTF-8 bytes, lower-hex. The server never stores the raw
    /// token — only this hash.
    /// </summary>
    public static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
