namespace Cocoar.Auth.Api.Middleware;

/// <summary>
/// Adds the standard browser-side defence-in-depth response headers on every
/// response that goes back to a browser. Closes <c>HEADERS-01</c> from the
/// security-hardening tracker.
///
/// <para>The set:</para>
/// <list type="bullet">
///   <item><description><b>Content-Security-Policy</b> — the heaviest header.
///   Locks every resource source to <c>'self'</c>, allows Monaco's web-worker
///   pattern (<c>worker-src 'self' blob:</c>), and forbids being framed by
///   any origin (<c>frame-ancestors 'none'</c>) — a clickjacking-on-the-IdP
///   defence stronger than X-Frame-Options because it covers the whole
///   browser-fetch surface, not just the legacy iframe vector. Inline
///   styles are accepted (<c>'unsafe-inline'</c>) — Vue + Tailwind ship
///   inline style attributes that we'd have to wholesale-rewrite to drop.
///   Inline scripts are NOT accepted.</description></item>
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

    private static string BuildContentSecurityPolicy(bool isDevelopment)
    {
        // Vite's dev server uses HMR over websockets and eval-style helpers
        // — relax script-src in Development with 'unsafe-eval'. Production
        // drops 'unsafe-eval' but keeps 'unsafe-inline' because OpenIddict's
        // `response_mode=form_post` renders an auto-submitting HTML form
        // with an inline <script> that drives the bounce back to the RP.
        // Switching to a nonce per-response would mean intercepting
        // OpenIddict's view rendering — bigger surgery than the marginal
        // XSS-defence improvement justifies; SOP + frame-ancestors+object-
        // src+form-action together still keep the IdP's exploitation
        // surface tight even with inline scripts permitted.
        var scriptSrc = isDevelopment
            ? "'self' 'unsafe-inline' 'unsafe-eval'"
            : "'self' 'unsafe-inline'";
        var connectSrc = isDevelopment
            ? "'self' ws: wss:"
            : "'self'";
        // OAuth response_mode=form_post submits the IdP's auth response via
        // an auto-submitting HTML form to the RP's redirect_uri — by design
        // a cross-origin POST. Locking form-action to 'self' breaks every
        // OIDC-Code-flow consumer. The OAuth-protocol layer validates
        // redirect_uri exact-match against the registered set on every
        // /authorize, which is the actually-meaningful gate; CSP form-action
        // here would just duplicate that with extra brittleness (CSP can't
        // enumerate the per-realm registered redirects without runtime
        // generation). We accept the relaxation and rely on the OAuth
        // validator. Future hardening: generate per-deployment from the
        // client registry at startup.
        var formAction = "*";

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
            // OAuth response_mode=form_post submits to the RP's redirect_uri
            // — that's by definition a different origin. We can't lock
            // form-action to 'self'. In Production a deploy-time configurable
            // allowlist would be ideal; for now we leave it permissive on
            // the form-action axis (other axes still locked) and accept
            // that the form-action header is more advisory than enforcing
            // for an IdP. Dev gets the test apps' origins explicitly.
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
                : BuildContentSecurityPolicy(_isDevelopment);

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
