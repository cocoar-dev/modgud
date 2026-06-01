namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm account-deletion policy, owned by the realm-admin. A sub-document
/// on the tenant-DB <c>RealmSettings</c> aggregate (stored as JSONB — adding
/// fields here needs no schema migration). Null on the parent = this realm has
/// never configured deletion; callers read it as <see cref="Defaults"/>.
///
/// <para>Replaces the previously hardcoded 7-day confirm-token window in the
/// GDPR service with an explicit, per-realm grace + recycle-bin model:</para>
/// <list type="bullet">
///   <item><see cref="GraceDays"/> — self-service: the user requests deletion,
///   stays able to log in and cancel for this many days, then the account is
///   auto-erased.</item>
///   <item><see cref="ReminderLeadDays"/> — how many days before the grace
///   deadline the "your account is about to be deleted" reminder is sent.</item>
///   <item><see cref="AdminRetentionDays"/> — admin recycle-bin: how long a
///   soft-deleted (deactivated) account is kept before it is eligible for
///   auto-purge.</item>
///   <item><see cref="AutoPurgeEnabled"/> — whether the scheduled job actually
///   empties the admin recycle bin at retention expiry, or whether an admin
///   must empty it manually.</item>
/// </list>
/// </summary>
public record DeletionSettings
{
    /// <summary>Self-service grace window in days (request → auto-erase).
    /// The user can log in and cancel any time within this window.</summary>
    public int GraceDays { get; init; } = 30;

    /// <summary>Days before the self-service grace deadline to send the
    /// reminder email. Must be &lt; <see cref="GraceDays"/> to fire.</summary>
    public int ReminderLeadDays { get; init; } = 2;

    /// <summary>Admin recycle-bin retention in days before an account becomes
    /// eligible for auto-purge.</summary>
    public int AdminRetentionDays { get; init; } = 30;

    /// <summary>When <c>true</c> (default), the scheduled job permanently
    /// erases admin recycle-bin accounts once <see cref="AdminRetentionDays"/>
    /// elapses. When <c>false</c>, the bin is only emptied by explicit admin
    /// action (ForceDelete).</summary>
    public bool AutoPurgeEnabled { get; init; } = true;

    /// <summary>Shared defaults used when a realm has never configured
    /// deletion. Matches the property initializers above.</summary>
    public static DeletionSettings Defaults { get; } = new();
}
