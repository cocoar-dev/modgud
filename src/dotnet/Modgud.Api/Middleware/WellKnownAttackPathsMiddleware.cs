using System.Text.RegularExpressions;

namespace Modgud.Api.Middleware;

/// <summary>
/// Short-circuits well-known attack-probe paths with a clean 404 instead of
/// letting them fall through to the SPA fallback handler that serves
/// <c>index.html</c> with HTTP 200 for any unmatched path.
///
/// <para>The SPA's catch-all behaviour is correct for client-side routing
/// (every Vue route is a virtual path under the same shell) but it muddies
/// security scanner output: probes for <c>/.git/config</c>,
/// <c>/server-status</c>, <c>/wp-admin</c> all return 200 + 1260-byte
/// SPA-shell, which scanners flag as "potentially exposed". No actual data
/// is exposed — every 200 response is byte-for-byte the same SPA shell —
/// but the noise drowns real findings in scanner reports.</para>
///
/// <para>This middleware returns a clean 404 for paths that match a
/// curated list of attack-probe patterns: VCS directories (<c>.git</c>,
/// <c>.svn</c>, <c>.hg</c>, <c>.bzr</c>), env / config dotfiles
/// (<c>.env*</c>, <c>.htaccess</c>, <c>.htpasswd</c>), credential
/// directories (<c>.aws</c>, <c>.ssh</c>, <c>id_rsa</c>), Apache mod_status
/// (<c>/server-status</c>, <c>/server-info</c>), WordPress probes
/// (<c>/wp-*</c>, <c>/xmlrpc.php</c>), PHP probes (<c>/phpinfo.php</c>,
/// <c>/phpmyadmin</c>), backup artifacts (<c>backup.*</c>, <c>dump.*</c>,
/// <c>*.sql</c>, <c>*.bak</c>), OS artifacts (<c>.DS_Store</c>,
/// <c>Thumbs.db</c>), IDE folders (<c>.vscode</c>, <c>.idea</c>,
/// <c>.npm</c>), and CGI (<c>cgi-bin</c>).</para>
///
/// <para>Allowlist: anything under <c>/.well-known/*</c> passes through
/// untouched. That namespace is reserved by RFC 8615 for legitimate
/// discovery (<c>openid-configuration</c>, <c>jwks</c>,
/// <c>security.txt</c>, <c>change-password</c>) and must not be 404'd
/// here.</para>
///
/// <para>Compiled to source-generated regex via <see cref="GeneratedRegexAttribute"/>
/// — the pattern is checked on every request, so a JIT-compiled DFA matters
/// for hot-path latency.</para>
/// </summary>
public sealed partial class WellKnownAttackPathsMiddleware
{
    private readonly RequestDelegate _next;

    public WellKnownAttackPathsMiddleware(RequestDelegate next) => _next = next;

    [GeneratedRegex(
        @"^/(" +
        // Version-control directories (real exposure path historically — git
        // repos served accidentally have leaked source + secrets)
        @"\.git(/|$)|\.svn(/|$)|\.hg(/|$)|\.bzr(/|$)|" +
        // Env / config dotfiles
        @"\.env(\.|$)|\.htaccess$|\.htpasswd$|" +
        // Credential directories — scanners probe these to find leaked SSH keys
        @"\.aws(/|$)|\.ssh(/|$)|id_rsa(\.pub)?$|" +
        // IDE / dev artifacts that occasionally end up deployed
        @"\.vscode(/|$)|\.idea(/|$)|\.npm(/|$)|" +
        // OS artifacts
        @"\.DS_Store$|Thumbs\.db$|" +
        // Apache mod_status / mod_info — disclose request-handler state
        @"server-status$|server-info$|" +
        // WordPress / PHP probes — we're neither, no legitimate hit
        @"wp-(admin|login|content|includes|config)|xmlrpc\.php|" +
        @"phpinfo\.php|phpmyadmin|pma(/|$)|" +
        // Backup / dump file extensions — typical accidental-deploy targets.
        // The .sql/.bak/.swp matches are restricted to root-level filenames
        // ([^/]+) so a future route like /api/admin/foo.sql wouldn't be
        // collateral-damaged.
        @"backup\.|dump\.|[^/]+\.sql$|[^/]+\.bak$|[^/]+\.swp$|" +
        // CGI-era artifacts
        @"cgi-bin(/|$)" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttackProbePattern();

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (!string.IsNullOrEmpty(path) && AttackProbePattern().IsMatch(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return _next(context);
    }
}
