namespace Modgud.Api.Middleware;

/// <summary>
/// CSRF defence-in-depth on state-changing <c>/api/*</c> requests. Targets
/// CSRF-02 (no global antiforgery middleware) and CSRF-03 (anonymous login
/// endpoints unprotected) without forcing the every-test-rewrite cost of a
/// full antiforgery-token rollout.
///
/// <para>The check uses the headers a modern browser attaches automatically
/// to every fetch / form submission, so it adds zero plumbing on the SPA
/// side and zero pre-fetch cost on the integration-test side.</para>
///
/// <para>Decision tree per state-changing /api/* request:</para>
/// <list type="number">
///   <item><description>If <c>Sec-Fetch-Site</c> is present (every browser
///   since 2020): accept <c>same-origin</c> and <c>same-site</c>; reject
///   <c>cross-site</c> with 403. <c>none</c> means top-level navigation
///   directly typed into the URL bar — accept (no Origin to compare to).</description></item>
///   <item><description>Else if <c>Origin</c> is present: must match the
///   request host. Reject 403 on mismatch.</description></item>
///   <item><description>Else if <c>Referer</c> is present: must match the
///   request host. Reject 403 on mismatch.</description></item>
///   <item><description>Else (no browser-attached headers — server-to-server,
///   curl, integration test, mobile native client): allow.</description></item>
/// </list>
///
/// <para>The "no browser headers → allow" branch is the conscious tradeoff
/// that makes this lighter than full antiforgery: a server-to-server caller
/// can bypass it. That's fine for our threat model — a server-to-server
/// caller doesn't carry a browser's session cookie, so even if it slips
/// through this gate it won't act as a logged-in user. The threats this
/// catches are exactly the ones a browser CAN execute: cross-site form
/// POST, fetch from evil.com, auto-fired JS on a tricked page.</para>
///
/// <para>Combined with the SameSite=Lax cookie (COOKIE-01), state-changing
/// cross-site requests get neither the cookie nor a passing CSRF check —
/// CSRF surface on /api/* is closed for browser-borne attacks.</para>
/// </summary>
public sealed class CsrfDefenseMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> StateChangingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "DELETE", "PATCH",
    };

    public CsrfDefenseMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldCheck(context))
        {
            await _next(context);
            return;
        }

        if (!IsRequestSameSite(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "csrf_blocked",
                message = "Cross-site state-changing requests are blocked. " +
                          "Use the SPA at the same origin or a server-to-server client.",
            });
            return;
        }

        await _next(context);
    }

    private static bool ShouldCheck(HttpContext context)
    {
        if (!StateChangingMethods.Contains(context.Request.Method)) return false;
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return false;
        // Scope: SPA data-plane and admin REST. /connect/* (OAuth) has its
        // own protocol-level protections (PKCE, state, id_token_hint).
        // /signin-oidc + /signout-callback-oidc are external IdP callbacks,
        // not user-initiated.
        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRequestSameSite(HttpContext context)
    {
        // Modern browsers attach Sec-Fetch-Site automatically. Trust it
        // first — it's the most reliable signal and not spoofable from
        // page-side JS.
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(fetchSite))
        {
            // "same-origin" / "same-site" → accept.
            // "none" → top-level navigation typed into URL bar — accept.
            // "cross-site" → reject.
            return fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                || fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase)
                || fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        // Fall back to Origin / Referer host matching for older browsers.
        var host = context.Request.Host.ToString();
        if (string.IsNullOrEmpty(host)) return true; // pathological — let it through

        bool MatchesHost(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return false;
            if (!Uri.TryCreate(headerValue, UriKind.Absolute, out var uri)) return false;
            var headerHost = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return string.Equals(headerHost, host, StringComparison.OrdinalIgnoreCase);
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return MatchesHost(origin);
        }

        var referer = context.Request.Headers.Referer.ToString();
        if (!string.IsNullOrEmpty(referer))
        {
            return MatchesHost(referer);
        }

        // No browser-attached headers at all → server-to-server caller.
        // Accept; cookie-bound auth still applies separately.
        return true;
    }
}
