using System.Security.Cryptography;
using System.Text;

namespace Modgud.Authentication.Registration;

/// <summary>How a pending registration proves control of the address.</summary>
public enum RegistrationProofKind
{
    /// <summary>A short numeric code typed back (native OTP paths).</summary>
    Code = 0,

    /// <summary>A signed link clicked from the mailbox (web self-registration).</summary>
    Link = 1,
}

/// <summary>Stable names for the path a registration came through — recorded on the
/// user (<c>ApplicationUser.RegistrationSource</c>) and in <c>UserRegisteredEvent</c>.</summary>
public static class RegistrationSources
{
    public const string NativeJit = "native-jit";
    public const string NativeExplicit = "native-explicit";
    public const string NativeInvite = "native-invite";
    public const string Web = "web";
}

/// <summary>
/// ADR 0018 — the ONE pre-verification record for every public sign-up path (web with
/// password, native OTP under JIT / invite-code posture, explicit native register).
/// No <c>ApplicationUser</c> exists until the proof succeeds; this document carries
/// everything needed to create the user at that moment.
///
/// <para><b>Identity.</b> The id is derived from the realm's normalized address
/// (<see cref="IdFor"/>), so there is at most one pending record per address. A
/// stranger typing someone else's address can never block the real owner: the owner's
/// own request simply overwrites the record and receives the proof. Requests for an
/// address that already belongs to a user never enter the pipeline at all.</para>
///
/// <para><b>Storage.</b> A plain Marten document with optimistic concurrency, like the
/// challenge documents — NOT event-sourced, NOT soft-deleted, never projected, never
/// audited with its payload. It is hard-deleted on proof (in the same unit of work that
/// creates the user), on expiry (sweep job) and on GDPR erasure of the address. After
/// that nothing in the database identifies the person who typed the address. The
/// user's event stream starts at the proof.</para>
/// </summary>
public sealed class PendingRegistration
{
    public Guid Id { get; set; }

    // ── Identity ─────────────────────────────────────────────────────────────
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }

    /// <summary>Password hash for the web path (hashed and validated at request time);
    /// null for passwordless native registrations.</summary>
    public string? PasswordHash { get; set; }

    // ── Context captured at request time ─────────────────────────────────────
    public Guid? ApplicationId { get; set; }
    public string? ClientId { get; set; }

    /// <summary>Same-origin continuation (web only) appended to the verification link.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>Public base URL the verification link is built against (web only).</summary>
    public string? LinkBaseUrl { get; set; }

    /// <summary>Realm default groups snapshotted at request time, attached on proof.</summary>
    public string[] DefaultGroupIds { get; set; } = [];

    /// <summary>Snapshot of <c>RequireAdminApproval</c>: the user is created
    /// <c>IsActive=false</c> and waits for an admin.</summary>
    public bool RequireAdminApproval { get; set; }

    /// <summary>Invite code consumed for this registration (ADR-0012), linked to the user on proof.</summary>
    public Guid? ConsumedInviteId { get; set; }

    /// <summary>One of <see cref="RegistrationSources"/>.</summary>
    public string Source { get; set; } = string.Empty;

    // ── Proof ────────────────────────────────────────────────────────────────
    public RegistrationProofKind ProofKind { get; set; }

    /// <summary>SHA-256 hex of the code / link token. The plaintext only ever lives in the mail.</summary>
    public string SecretHash { get; set; } = string.Empty;

    public int Attempts { get; set; }

    /// <summary>0 = unlimited (link tokens carry 256 bits; only codes need a cap).</summary>
    public int MaxAttempts { get; set; }

    // ── Lifecycle / throttle state ───────────────────────────────────────────
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public int SendCount { get; set; }

    /// <summary>Set by the version-checked consume; the row is deleted right after the
    /// user exists. A consumed row that survived a crash is swept like an expired one.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsConsumed => ConsumedAt is not null;
    public bool HasExceededAttempts => MaxAttempts > 0 && Attempts >= MaxAttempts;

    /// <summary>Canonical address form used for the id and the uniqueness checks.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    /// <summary>
    /// Deterministic id for an address: the first 16 bytes of SHA-256 over the normalized
    /// address, shaped as a version-4-style GUID. Within a realm database this makes
    /// "one pending per address" a property of the key, not of a query.
    /// </summary>
    public static Guid IdFor(string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeEmail(email)));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        // Mark as a random-style GUID so it never collides with sequential ids.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
