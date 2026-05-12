namespace Cocoar.Auth.Authentication.SelfRegistration.Domain;

/// <summary>
/// Marten document storing the email-verification token for a
/// self-registered user. One row per pending registration; consumed and
/// soft-removed on /verify-email click. Tenant-scoped (lives in the
/// realm's tenant DB).
///
/// <para>Token shape mirrors <c>PendingAdminInvite</c>: 32 random bytes
/// → Base64Url, stored as SHA-256 hex hash. Plaintext only ever lives
/// in the magic-link URL.</para>
/// </summary>
public class PendingSelfRegistration
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Snapshot of the realm's <c>DefaultGroupIds</c> at register
    /// time. Attached to the user on /verify-email consume. Snapshotting
    /// avoids race conditions where the admin changes the realm setting
    /// between register and verify.</summary>
    public string[] DefaultGroupIds { get; set; } = [];

    /// <summary>Snapshot of <c>RequireAdminApproval</c>. When true, even
    /// after verification the user stays inactive until an admin
    /// approves.</summary>
    public bool RequireAdminApproval { get; set; }

    public bool IsUsed => UsedAt is not null;
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

    public const int DefaultExpirationHours = 24;
}
