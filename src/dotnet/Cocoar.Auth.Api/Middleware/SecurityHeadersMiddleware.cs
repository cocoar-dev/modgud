namespace Cocoar.Auth.Api.Middleware;

/// <summary>
/// Adds the standard browser-side defence-in-depth response headers on every
/// response that goes back to a browser. Closes <c>HEADERS-01</c> from the
/// security-hardening tracker.
///
/// <para>The set:</para>
/// <list type="bullet">
///   <item><description><b>Content-Security-Policy</b> — the heaviest header.
///   <b>Path-aware</b>: SPA pages get strict <c>script-src 'self'</c> and
///   <c>form-action 'self'</c>; only the <c>/connect/*</c> OIDC endpoints
///   loosen those two directives because OpenIddict's
///   <c>response_mode=form_post</c> renders an auto-submitting HTML form
///   with an inline <c>&lt;script&gt;</c> targeting a cross-origin RP
///   redirect_uri. Other directives are uniform: <c>frame-ancestors 'none'</c>
///   (clickjacking defence stronger than X-Frame-Options), <c>object-src
///   'none'</c>, Monaco web-worker via <c>worker-src 'self' blob:</c>,
///   inline styles permitted (Vue + Tailwind ship inline style attributes
///   we'd have to wholesale-rewrite to drop).</description></item>
///   <item><description><b>X-Content-Type-Options: nosniff</b> — older
///   browsers, but cheap and standard.</description></item>
///   <item><description><b>X-Frame-Options: DENY</b> — redundant with the
///   CSP <c>frame-ancestors</c>; kept because some scanners still flag its
///   absence.</description></item>
///   <item><description><b>Referrer-Policy: strict-origin-when-cross-origin</b>
///   — the default for SSO contexts. Same-origin gets full URL, cross-origin
///   gets origin-only, downgrade gets nothing.</description></item>
///   <item><description><b>Permissions-Policy</b> — disable
///   geolocation/microphone/camera/USB/etc. The IdP doesn't need them; if a
///   future feature does, allowlist explicitly here.</description></item>
///   <item><description><b>Cross-Origin-Opener-Policy: same-origin</b> —
///   process isolation against Spectre-class side channels. Same-origin
///   means popups can't share a renderer with attacker content.</description></item>
///   <item><description><b>Cache-Control / Pragma / Expires on <c>/api/*</c></b>
///   — auth-bearing JSON must never sit in browser/CDN caches. Closes the
///   back-button-after-logout leak on shared machines. <c>/connect/*</c> is
///   left to OpenIddict's own RFC-6749-compliant headers; static assets
///   keep their existing static-file-middleware cache hints.</description></item>
/// </list>
///
/// <para>Headers are applied via the <c>OnStarting</c> callback so they're
/// emitted even when an exception handler short-circuits the response.</para>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _isDevelopment;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
    {
        _next = next;
        _isDevelopment = env.IsDevelopment();
    }

    // Paths under OpenIddict's OIDC server. response_mode=form_post renders
    // an auto-submitting HTML form to the RP's redirect_uri — that requires
    // both an inline <script> (script-src 'unsafe-inline') AND a cross-
    // origin form action (form-action *). Anywhere else, neither is needed.
    private static bool IsOidcServerPath(PathString path)
        => path.StartsWithSegments("/connect");

    private static string BuildContentSecurityPolicy(bool isDevelopment, PathString path)
    {
        var isOidc = IsOidcServerPath(path);

        // Vite's dev server uses HMR over websockets and eval-style helpers
        // — relax script-src in Development with 'unsafe-eval'. Production
        // SPA pages get strict 'self' (Vue 3 production builds emit module
        // scripts only, no inline <script>). The /connect/* OIDC endpoints
        // need 'unsafe-inline' for OpenIddict's form_post auto-submitter;
        // they're the only paths that get the relaxation.
        var scriptSrc = isDevelopment
            ? "'self' 'unsafe-inline' 'unsafe-eval'"
            : isOidc ? "'self' 'unsafe-inline'" : "'self'";

        var connectSrc = isDevelopment
            ? "'self' ws: wss:"
            : "'self'";

        // OAuth response_mode=form_post submits to the RP's redirect_uri —
        // by design a cross-origin POST. Locking form-action to 'self'
        // breaks every OIDC-Code-flow consumer of /connect/authorize. The
        // OAuth-protocol layer validates redirect_uri exact-match against
        // the registered set on every /authorize, which is the actually-
        // meaningful gate; CSP form-action there would just duplicate that
        // with extra brittleness (CSP can't enumerate the per-realm
        // registered redirects without runtime generation). For SPA pages
        // (everything else) form-action 'self' is the right setting —
        // an injected form on a /login or /admin page should never be
        // allowed to submit cross-origin.
        var formAction = isOidc ? "*" : "'self'";

        return string.Join("; ", new[]
        {
            "default-src 'self'",
            $"script-src {scriptSrc}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            $"connect-src {connectSrc}",
            "font-src 'self' data:",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            $"form-action {formAction}",
            // Monaco web-worker uses blob: URIs for its language services.
            "worker-src 'self' blob:",
            // Disallow object/embed entirely — the IdP doesn't host plugins.
            "object-src 'none'",
        });
    }

    private static readonly string PermissionsPolicy = string.Join(", ", new[]
    {
        "geolocation=()",
        "microphone=()",
        "camera=()",
        "magnetometer=()",
        "gyroscope=()",
        "accelerometer=()",
        "payment=()",
        "usb=()",
        "fullscreen=(self)",
    });

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            // Idempotent — don't override values an upstream component may
            // have set (e.g. an admin endpoint deliberately loosening CSP
            // for a specific document).
            headers["X-Content-Type-Options"] = headers["X-Content-Type-Options"].Count > 0
                ? headers["X-Content-Type-Options"]
                : "nosniff";

            headers["X-Frame-Options"] = headers["X-Frame-Options"].Count > 0
                ? headers["X-Frame-Options"]
                : "DENY";

            headers["Referrer-Policy"] = headers["Referrer-Policy"].Count > 0
                ? headers["Referrer-Policy"]
                : "strict-origin-when-cross-origin";

            headers["Permissions-Policy"] = headers["Permissions-Policy"].Count > 0
                ? headers["Permissions-Policy"]
                : PermissionsPolicy;

            headers["Cross-Origin-Opener-Policy"] = headers["Cross-Origin-Opener-Policy"].Count > 0
                ? headers["Cross-Origin-Opener-Policy"]
                : "same-origin";

            headers["Content-Security-Policy"] = headers["Content-Security-Policy"].Count > 0
                ? headers["Content-Security-Policy"]
                : BuildContentSecurityPolicy(_isDevelopment, context.Request.Path);

            // Cache-Control on /api/* — auth-bearing JSON should never sit
            // in a browser back/forward cache, browser HTTP cache, or
            // intermediary CDN/proxy. Without this, /api/account/me is a
            // back-button leak risk on shared machines (logout doesn't
            // invalidate cached response). Trio = no-store (modern), Pragma
            // (HTTP/1.0 proxies), Expires:0 (legacy CDN). Static assets
            // (/assets/*, /favicon.ico) and the SPA shell keep their
            // existing static-file-middleware cache hints. /connect/* is
            // skipped — OpenIddict already emits its own no-store/Pragma
            // per RFC 6749 §5.1.
            if (context.Request.Path.StartsWithSegments("/api")
                && headers["Cache-Control"].Count == 0)
            {
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "0";
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
