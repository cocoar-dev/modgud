namespace Modgud.Authentication.Domain;

/// <summary>
/// ADR 0026 — a test account's fixed e-mail code: the regular e-mail-code sign-in
/// accepts it instead of a sent code. One row per account (<see cref="Id"/> = user id),
/// a plain document with optimistic concurrency, so the failure counter cannot clobber
/// a concurrent write. Never in an event, never in a manifest; deleted with the marker.
/// </summary>
public class TestAccountEmailCode
{
    public Guid Id { get; set; }

    /// <summary>Identity password hash of the six-digit code.</summary>
    public string CodeHash { get; set; } = "";

    /// <summary>Optional end of validity; null = no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset SetAt { get; set; }
    public Guid? SetByUserId { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedClientId { get; set; }

    /// <summary>Consecutive wrong codes since the last success.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Progressive delay: no attempt is checked before this instant.</summary>
    public DateTimeOffset? RetryNotBefore { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;
}
