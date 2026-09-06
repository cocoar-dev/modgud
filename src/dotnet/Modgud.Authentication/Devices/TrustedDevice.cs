namespace Modgud.Authentication.Devices;

/// <summary>
/// ADR 0020 — "a browser that has completed a login here". Created when a user
/// signs in interactively, renewed on every further success, bound to the
/// <c>Modgud.Device</c> cookie by <see cref="Id"/>. A device is <em>trusted for a
/// user</em> only if that user's id is in <see cref="UserIds"/>; someone else's
/// cookie therefore buys an attacker nothing beyond "untrusted".
///
/// <para><b>Storage rule.</b> A plain Marten document in the realm database — not
/// event-sourced, not soft-deleted, hard-deleted by the hourly sweep once idle for
/// <see cref="IdleLifetime"/>. GDPR erasure removes the user from every device; a
/// device left without users is deleted. Nothing in it identifies the browser
/// beyond a random id: no fingerprint, no user agent.</para>
/// </summary>
public sealed class TrustedDevice
{
    public const string CookieName = "Modgud.Device";

    /// <summary>How long a device stays trusted without any successful login.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(90);

    /// <summary>Bound so a shared kiosk cannot accumulate the whole realm.</summary>
    public const int MaxUsers = 10;

    public Guid Id { get; set; }

    /// <summary>Users that completed a login from this device, most recent last.</summary>
    public List<Guid> UserIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public bool IsTrustedFor(Guid userId) => UserIds.Contains(userId);

    /// <summary>Adds (or moves to the end) the user and stamps <see cref="LastSeenAt"/>.</summary>
    public void Touch(Guid userId, DateTimeOffset now)
    {
        UserIds.Remove(userId);
        UserIds.Add(userId);
        while (UserIds.Count > MaxUsers) UserIds.RemoveAt(0);
        LastSeenAt = now;
    }
}
