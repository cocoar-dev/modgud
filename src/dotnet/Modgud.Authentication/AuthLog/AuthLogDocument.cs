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
}
