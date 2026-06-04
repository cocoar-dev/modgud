namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm tenant-audit policy, owned by the realm-admin. A nullable JSONB
/// sub-record on the tenant-DB <c>RealmSettings</c> aggregate (adding fields needs
/// no migration). Null on the parent = never configured; callers read it as
/// <see cref="Defaults"/>.
///
/// <para><b>Visibility window, NOT retention/deletion.</b> The audit trail is a
/// rebuildable projection (<c>AuthAuditView</c>) over event streams we keep for the
/// aggregate's lifetime (masked on erase). This window only bounds what the read
/// surface *shows* — it does not delete history. Named <c>VisibilityWindowDays</c>
/// (not "RetentionDays") on purpose, so a realm-admin reading the setting can't
/// mistake it for a deletion guarantee — see the design doc §A.6.</para>
/// </summary>
public record AuditSettings
{
    /// <summary>How many days back the tenant audit read surface shows. Older rows
    /// are hidden from the view (not deleted). Must be at least 1.</summary>
    public int VisibilityWindowDays { get; init; } = 90;

    /// <summary>Shared defaults used when a realm has never configured the audit
    /// window. Matches the property initializer above.</summary>
    public static AuditSettings Defaults { get; } = new();
}
