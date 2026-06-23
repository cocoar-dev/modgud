namespace Modgud.Authentication.SelfRegistration.Domain;

/// <summary>
/// Marten document for a single-use registration invite code (ADR-0012).
/// Under the per-Application <c>SelfRegPosture.InviteCode</c>, an unknown
/// email becomes a passwordless user <em>only</em> when the native sign-up
/// request carries a valid, unused, unexpired code that matches one of these.
/// Tenant-scoped (lives in the realm's tenant DB).
///
/// <para>The split (ADR-0012): Modgud decides <em>who may exist</em>, the
/// consuming app decides <em>who may do what</em>. Modgud only ever learns
/// <c>(email, code, appId)</c> — never what the invite is for.</para>
///
/// <para>Code shape mirrors <c>PendingSelfRegistration</c> / <c>PendingAdminInvite</c>:
/// the plaintext (a ~128-bit base64url token, D12) is returned to the minting
/// caller exactly once; only its SHA-256 hex hash is stored. The doc is
/// consumed atomically at account-creation time (D4) under optimistic
/// concurrency so single-use holds even for bearer codes.</para>
/// </summary>
public class RegistrationInviteCode
{
    public Guid Id { get; set; }

    /// <summary>App this code belongs to (D3, required). The code redeems into
    /// this app's posture/branding, and minting isolation is enforced against
    /// it (the <c>{appId}</c> path must equal the minting scope's app).</summary>
    public Guid AppId { get; set; }

    /// <summary>SHA-256 hex of the plaintext code. The hot lookup key.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Optional normalized email this code is bound to (D2). When set,
    /// the code only redeems for that recipient; <c>null</c> = bearer (the
    /// default, most application-agnostic variant).</summary>
    public string? BoundEmail { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Subject that minted the code — the ServiceAccount id or the
    /// admin sub. Audit only.</summary>
    public string CreatedBySubject { get; set; } = string.Empty;

    /// <summary><c>null</c> = open; set = consumed (single-use).</summary>
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByUserId { get; set; }

    public bool IsUsed => UsedAt is not null;
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

    /// <summary>Default code TTL when the minting caller doesn't override it (D10).</summary>
    public const int DefaultExpirationDays = 14;
}
