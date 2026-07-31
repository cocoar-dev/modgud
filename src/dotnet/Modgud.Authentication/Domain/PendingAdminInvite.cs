namespace Modgud.Authentication.Domain;

/// <summary>
/// One-shot invitation for a new realm administrator (C15). Stored in the
/// tenant DB; issued by the Control-Plane API or the recovery CLI
/// <c>bootstrap-admin</c> command (no <c>--password</c> flag).
///
/// <para>Single-use: <see cref="UsedAt"/> is set when the recipient's
/// password-set form succeeds. A second submit with the same token is
/// rejected — this is also the chain-revocation hook for any future
/// "token leaked"-style mitigation.</para>
///
/// <para>Stored as SHA-256 hash, never the raw token. The plaintext lives
/// only in the Magic-Link URL emailed to / printed for the recipient.
/// Same shape as <see cref="MagicLinkChallenge"/>.</para>
/// </summary>
public class PendingAdminInvite
{
    public Guid Id { get; set; }

    /// <summary>
    /// Username the admin will get on first password-set. Picked at
    /// invite-issue time so the recipient lands on a deterministic
    /// account — they can't pick a different username themselves.
    /// </summary>
    public string UserName { get; set; } = "";

    /// <summary>
    /// Email address of the recipient. Used as the user's email on
    /// account creation AND as the trust anchor: only someone who
    /// receives mail at this address (or who can read the magic-link
    /// printed on stdout in the CLI case) can complete the bootstrap.
    /// </summary>
    public string Email { get; set; } = "";

    public string? Firstname { get; set; }
    public string? Lastname { get; set; }

    public string TokenHash { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set when the bootstrap-form successfully consumes the token.
    /// Once non-null, every further submit with this token is rejected.
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>
    /// Optional: who issued the invite. <c>null</c> for self-service
    /// CLI bootstrap; the issuing CP-admin's username when issued via
    /// <c>POST /api/admin/realms</c>. For audit only.
    /// </summary>
    public string? IssuedBy { get; set; }

    public const int DefaultExpirationHours = 24;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsUsed => UsedAt.HasValue;
}
