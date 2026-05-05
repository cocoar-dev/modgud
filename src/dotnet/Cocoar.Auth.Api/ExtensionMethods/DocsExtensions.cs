using Microsoft.AspNetCore.StaticFiles;
using Cocoar.Auth.Api.Helper;

namespace Cocoar.Auth.Api.ExtensionMethods;

/// <summary>
/// Serves the end-user VitePress documentation under /docs/ from wwwroot/docs/.
/// Any authenticated user may read the docs — unauthenticated requests get a 302
/// redirect to /login?redirect=&lt;path&gt; so the browser flow matches any other
/// protected page. After login, LoginView uses a full-page navigation (not router.push)
/// so the static HTML takes over.
///
/// One middleware does everything: gate → directory normalization → file lookup →
/// serve / 404. No MapWhen / UsePathBase / RequestPath juggling.
/// </summary>
public class DocsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _docsRoot;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public DocsMiddleware(RequestDelegate next)
    {
        _next = next;
        _docsRoot = Path.GetFullPath(Path.Combine(PathHelper.GetFullPath("wwwroot"), "docs"));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/docs", out var rest))
        {
            await _next(context);
            return;
        }

        // Auth gate.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            // SCS0027: the redirect target is the literal string "/login"
            // followed by an escaped query parameter. Same-origin by
            // construction — no leading slash from request data, no host,
            // no scheme. The downstream consumer (LoginView) validates
            // the `redirect` param is a same-origin path before navigating.
            var redirectTarget = context.Request.Path + context.Request.QueryString;
#pragma warning disable SCS0027
            context.Response.Redirect($"/login?redirect={Uri.EscapeDataString(redirectTarget)}");
#pragma warning restore SCS0027
            return;
        }

        // Only GET/HEAD make sense for static docs.
        if (context.Request.Method != HttpMethods.Get && context.Request.Method != HttpMethods.Head)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // Docs root doesn't exist (typical in Development — docs only get built into the
        // Docker image). Fall through so the outer pipeline can return its usual SPA shell.
        if (!Directory.Exists(_docsRoot))
        {
            await _next(context);
            return;
        }

        // Map URL path → file. Resolve and run the path-traversal guard
        // BEFORE any filesystem call so a `/docs/../../something` request
        // never even probes anything outside the docs root.
        var relative = rest.Value?.TrimStart('/') ?? "";
        var resolvedPath = Path.GetFullPath(Path.Combine(_docsRoot, relative));

        // Path-traversal guard.
        if (!resolvedPath.StartsWith(_docsRoot, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // After the guard the path is provably inside _docsRoot — directory
        // probes for the index.html resolution can no longer touch foreign
        // dirs, so the user-input → FS flow is mediated.
#pragma warning disable CA3003
        if (string.IsNullOrEmpty(relative) || Directory.Exists(resolvedPath))
        {
            resolvedPath = Path.GetFullPath(Path.Combine(resolvedPath, "index.html"));
            // Re-check after the index.html append in case _docsRoot was a
            // symlink or the combine produced an edge-case path.
            if (!resolvedPath.StartsWith(_docsRoot, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }

        if (!File.Exists(resolvedPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
#pragma warning restore CA3003

        var filePath = resolvedPath;

        // Content type + cache headers. VitePress hashes asset filenames, so `.xxxxxx.js`
        // and `.xxxxxx.css` can be cached forever; everything else stays no-cache so users
        // see fresh docs immediately after a redeploy.
        if (!_contentTypes.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }
        context.Response.ContentType = contentType;

        context.Response.Headers.CacheControl = IsHashedAsset(filePath)
            ? "public, max-age=31536000, immutable"
            : "no-cache, no-store";

        await context.Response.SendFileAsync(filePath);
    }

    private static readonly System.Text.RegularExpressions.Regex HashedAssetPattern =
        new(@"\.[a-zA-Z0-9_-]{6,}\.(css|js)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsHashedAsset(string path) =>
        HashedAssetPattern.IsMatch(Path.GetFileName(path));
}

public static class DocsExtensions
{
    public static void UseDocs(this WebApplication app) =>
        app.UseMiddleware<DocsMiddleware>();
}
