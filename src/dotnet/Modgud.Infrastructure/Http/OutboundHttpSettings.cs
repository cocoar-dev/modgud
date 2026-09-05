namespace Modgud.Infrastructure.Http;

/// <summary>
/// Deployment-wide settings for Modgud's server-side fetches of admin-supplied
/// URLs (OIDC discovery and token endpoints, SAML metadata, client-id metadata
/// documents, back-channel logout delivery). Bound from configuration section
/// <c>OutboundHttp</c>; env form <c>OutboundHttp__AllowedPrivateHosts</c>.
/// </summary>
public class OutboundHttpSettings
{
    /// <summary>
    /// Hostnames the SSRF guard may connect to even when they resolve to a
    /// private, loopback or otherwise non-public address: an identity provider
    /// or a resource server on the internal network. Separated by comma,
    /// semicolon or whitespace; exact host, or <c>*.example.internal</c> for a
    /// whole suffix. Set by the platform operator only — a realm admin cannot
    /// widen it, which is the point: the guard exists because realm admins are
    /// a lower trust tier than the operator.
    /// </summary>
    public string AllowedPrivateHosts { get; set; } = "";
}

/// <summary>
/// The parsed form of <see cref="OutboundHttpSettings.AllowedPrivateHosts"/>,
/// consulted by <see cref="SsrfSafeHttpHandlerFactory"/> before it refuses a
/// non-public address. Immutable; one instance per host.
/// </summary>
public sealed class SsrfAllowList
{
    public static readonly SsrfAllowList Empty = new([], []);

    private readonly HashSet<string> _exact;
    private readonly string[] _suffixes;

    private SsrfAllowList(HashSet<string> exact, string[] suffixes)
    {
        _exact = exact;
        _suffixes = suffixes;
    }

    /// <summary>Every host the operator listed, for logs and diagnostics.</summary>
    public IReadOnlyCollection<string> Entries { get; private init; } = [];

    public bool IsEmpty => _exact.Count == 0 && _suffixes.Length == 0;

    public static SsrfAllowList Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;

        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suffixes = new List<string>();
        var entries = new List<string>();
        foreach (var raw in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.TrimEnd('.').ToLowerInvariant();
            if (entry.Length == 0) continue;
            entries.Add(entry);
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                if (entry.Length > 2) suffixes.Add(entry[1..]); // ".example.internal"
            }
            else
            {
                exact.Add(entry);
            }
        }
        return new SsrfAllowList(exact, suffixes.ToArray()) { Entries = entries };
    }

    /// <summary>
    /// True when <paramref name="host"/> is listed exactly or matches a
    /// <c>*.suffix</c> entry. A suffix entry never matches its bare apex.
    /// </summary>
    public bool Allows(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var h = host.TrimEnd('.');
        if (_exact.Contains(h)) return true;
        foreach (var suffix in _suffixes)
        {
            if (h.Length > suffix.Length && h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
