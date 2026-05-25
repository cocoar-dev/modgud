namespace Modgud.Authentication.Identity;

/// <summary>
/// PII masking helpers for log lines. Use these whenever a piece of
/// personal data ends up in a structured-log message — emails in
/// rejection paths, usernames in failed-login traces, etc. — so the
/// PII surface in the centralised log infrastructure (SIEM, support
/// dashboards, third-party aggregators) stays minimal while the log
/// remains useful for ops triage.
///
/// <para>The masking is intentionally one-way and stateless. We don't
/// hash with a key (that would let a determined viewer dictionary-attack
/// known-bad inputs offline), and we don't pseudonymise per-tenant
/// (out of scope for log triage). The goal is "useful enough to debug,
/// useless to harvest at scale".</para>
/// </summary>
public static class LogPiiMasking
{
    /// <summary>
    /// Mask an email so the local-part doesn't leak. Keeps the first
    /// character of the local-part for ops triage ("starts with j…")
    /// and the full domain (which is needed to debug allowlist mismatches
    /// + tenant routing). Empty/null/malformed inputs return a neutral
    /// placeholder so log messages stay parseable.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "(none)";
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "(invalid)";
        var local = email.AsSpan(0, at);
        var domain = email.AsSpan(at + 1);
        var firstChar = local[0];
        return $"{firstChar}***@{domain}";
    }
}
