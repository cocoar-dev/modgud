using Microsoft.AspNetCore.StaticFiles;
using TimeToDo.Api.Helper;

namespace TimeToDo.Api.ExtensionMethods;

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
            var redirectTarget = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"/login?redirect={Uri.EscapeDataString(redirectTarget)}");
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

        // Map URL path → file. Directories resolve to index.html.
        var relative = rest.Value?.TrimStart('/') ?? "";
        var filePath = Path.Combine(_docsRoot, relative);

        if (string.IsNullOrEmpty(relative) || Directory.Exists(filePath))
        {
            filePath = Path.Combine(filePath, "index.html");
        }

        filePath = Path.GetFullPath(filePath);

        // Path-traversal guard — reject resolved paths outside the docs root.
        if (!filePath.StartsWith(_docsRoot, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!File.Exists(filePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

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
