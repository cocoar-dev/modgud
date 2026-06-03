namespace Modgud.Authentication.AuthLog;

/// <summary>
/// Marten document for persisted auth log entries.
///
/// <para><b>Retention &amp; GDPR (LOG-02 / security-hardening tracker):</b></para>
/// <list type="bullet">
///   <item><description><b>Retention window:</b> 7 days. The
///   <c>AuthLogPersistenceService</c> background worker prunes records
///   older than 7 days on each iteration. After that, the entry is gone
///   from the database — recovery from older logs requires a Postgres
///   backup, which is operationally separate.</description></item>
///   <item><description><b>What's persisted:</b> timestamp, log level,
///   message text, optional user-name (if the calling principal had one),
///   optional source IP. Specifically NOT persisted: cookies, tokens,
///   secrets, full request/response bodies, password values. Auth
///   endpoints log <c>{UserName}</c> + <c>{IP}</c> at Information level
///   for security-event traceability — this is the canonical "who tried
///   what from where" log under the GDPR legitimate-interest basis for
///   detecting and responding to credential abuse.</description></item>
///   <item><description><b>Access control:</b> read access via the
///   <c>modgud:auth-log:read</c> permission, which the seeded
///   <c>help-desk</c> role carries. No public-network exposure of the
///   raw documents.</description></item>
///   <item><description><b>Erasure obligations:</b> when a user invokes
///   GDPR-erasure, their personal references in <c>UserName</c> are
///   masked at the <c>ArchiveStream</c> layer; the entry stays as the
///   security-audit record but is no longer linkable to the individual.
///   Source-IP is treated as personal data and falls under the same
///   7-day retention window as the rest.</description></item>
/// </list>
/// </summary>
public class AuthLogDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "Info";
    public string Message { get; init; } = "";
    public string? UserName { get; init; }
    public string? Ip { get; init; }

    /// <summary>
    /// The realm slug the event was emitted in (captured from the ambient
    /// <c>TenantContext</c> by <see cref="RealmLogEnricher"/> at log time;
    /// background / no-tenant work is attributed to <c>system</c>). All entries
    /// live in the system DB; this column is what scopes the admin read so a
    /// tenant realm-admin sees only their own realm's events while the
    /// control-plane realm sees the full cross-realm log. Null only on legacy
    /// rows written before this column existed.
    /// </summary>
    public string? Realm { get; init; }
}
