using System.Security.Claims;
using System.Security.Cryptography;
using BuildingBlocks.Helper;
using Cocoar.Auth.Application.Assets;
using Cocoar.Auth.Application.DTOs.Assets;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Domain.Assets;
using Cocoar.Auth.Domain.RealmSettings;
using Marten;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Cocoar.Auth.Api.Features.Admin;

/// <summary>
/// Per-realm asset library endpoints. Uploads are admin-gated; the public
/// read endpoint at <c>/api/assets/{id}</c> is anonymous so the login page can
/// fetch branding-logos before the user authenticates.
///
/// <para>Storage: BYTEA in the tenant DB. Backups are self-contained,
/// failover is transactional, and there's no extra filesystem mount to
/// keep alive. The 2 MB-per-asset cap keeps row size sane.</para>
/// </summary>
public static class AssetsEndpoints
{
    public static WebApplication MapAssetsEndpoints(this WebApplication application, string path)
    {
        var admin = application.MapGroup($"{path}/admin/assets")
            .WithTags("Admin Assets")
            .RequireAuthorization();

        // ── List ─────────────────────────────────────────────────────────
        admin.MapGet("", async (IQuerySession session, CancellationToken ct) =>
        {
            var docs = await session.Query<Asset>()
                .OrderByDescending(a => a.UploadedAt)
                .Take(500)
                .ToListAsync(ct);
            return Results.Ok(docs.Select(ToDto));
        })
        .RequiresPermission("asset:read")
        .WithName("Admin_Assets_List");

        // ── Upload (multipart) ───────────────────────────────────────────
        admin.MapPost("", async (
            HttpContext http,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            // Allow up to MaxSizeBytes + envelope overhead. ASP.NET Core's
            // default is 30 MB; tightening it caps mass-storage abuse via
            // a single bloated request.
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()
                ?.MaxRequestBodySize = AssetValidation.MaxSizeBytes + 64 * 1024;

            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { Message = "multipart/form-data required" });

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { Message = "Empty upload" });
            if (file.Length > AssetValidation.MaxSizeBytes)
                return Results.BadRequest(new
                {
                    Message = $"File too large; max {AssetValidation.MaxSizeBytes} bytes.",
                });

            byte[] bytes;
            await using (var ms = new MemoryStream(capacity: (int)file.Length))
            {
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }

            var detected = AssetValidation.SniffContentType(bytes);
            if (detected is null || !AssetValidation.AllowedMimeTypes.Contains(detected))
                return Results.BadRequest(new
                {
                    Message = "Unsupported file type. Allowed: PNG, JPEG, GIF, WebP, SVG, ICO.",
                });

            if (detected == "image/svg+xml")
            {
                var sanitized = AssetValidation.SanitizeSvg(bytes);
                if (sanitized.IsError)
                    return Results.BadRequest(new { Message = sanitized.FirstError.Description });
                bytes = sanitized.Value;
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var asset = new Asset
            {
                Id = Guid.NewGuid(),
                FileName = SanitizeFileName(file.FileName),
                ContentType = detected,
                SizeBytes = bytes.LongLength,
                Sha256 = sha,
                Data = bytes,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedByUserId = ResolveUserId(http.User),
                UploadedByUsername = http.User.FindFirstValue(ClaimTypes.Name) ?? http.User.Identity?.Name,
            };

            session.Store(asset);
            await session.SaveChangesAsync(ct);

            return Results.Created($"/api/assets/{ShortGuid.Encode(asset.Id)}", ToDto(asset));
        })
        .RequiresPermission("asset:write")
        .WithName("Admin_Assets_Upload")
        .DisableAntiforgery();

        // ── Delete (blocked if referenced) ───────────────────────────────
        admin.MapDelete("{id}", async (
            string id,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            var assetId = ShortGuid.Decode(id);
            var asset = await session.LoadAsync<Asset>(assetId, ct);
            if (asset is null) return Results.NotFound();

            var refs = await FindReferencesAsync(session, assetId, ct);
            if (refs.Count > 0)
            {
                return Results.Conflict(new AssetInUseDto
                {
                    Id = ShortGuid.Encode(assetId),
                    References = refs,
                });
            }

            session.Delete<Asset>(assetId);
            await session.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequiresPermission("asset:write")
        .WithName("Admin_Assets_Delete");

        // ── Public read ──────────────────────────────────────────────────
        // Anonymous: the login page references branding-assets before the
        // user is authenticated. Per-realm via TenantContext (asset only
        // resolvable from the realm's own DB). Long-lived immutable cache
        // since the GUID-keyed payload is content-stable for its lifetime.
        //
        // URL sits under `/api/assets/` so it shares the existing Vite proxy
        // (no new proxy entry needed in dev) and stays distinct from Vite's
        // own `/assets/` chunk-output directory — which would otherwise let
        // this parametric route swallow the SPA's JS chunks in production.
        application.MapGet("/api/assets/{id}", async (
            string id,
            IQuerySession session,
            HttpContext http,
            CancellationToken ct) =>
        {
            Guid assetId;
            try { assetId = ShortGuid.Decode(id); }
            catch { return Results.NotFound(); }

            var asset = await session.LoadAsync<Asset>(assetId, ct);
            if (asset is null) return Results.NotFound();

            // GUID-keyed payloads are content-stable: long-cache + immutable.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

            // EntityTag drives 304 via the framework's File-result. Built from
            // the SHA-256 so identical bytes get the same tag across realms.
            var etag = new EntityTagHeaderValue($"\"{asset.Sha256}\"");
            return Results.File(asset.Data, asset.ContentType, entityTag: etag);
        })
        .WithName("Assets_PublicRead")
        .AllowAnonymous();

        return application;
    }

    /// <summary>Realm-Settings is currently the only Branding-shaped consumer.
    /// As more places start referencing assets (login providers, email
    /// templates, page-builder), extend this list.</summary>
    private static async Task<IReadOnlyList<string>> FindReferencesAsync(
        IQuerySession session, Guid assetId, CancellationToken ct)
    {
        var settings = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
        var refs = new List<string>();
        if (settings?.Branding?.LogoAssetId == assetId) refs.Add("branding.logo");
        if (settings?.Branding?.FaviconAssetId == assetId) refs.Add("branding.favicon");
        return refs;
    }

    private static AssetDto ToDto(Asset a) => new()
    {
        Id = ShortGuid.Encode(a.Id),
        FileName = a.FileName,
        ContentType = a.ContentType,
        SizeBytes = a.SizeBytes,
        Sha256 = a.Sha256,
        UploadedAt = a.UploadedAt,
        UploadedByUsername = a.UploadedByUsername,
        Url = $"/api/assets/{ShortGuid.Encode(a.Id)}",
    };

    private static Guid? ResolveUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string SanitizeFileName(string raw)
    {
        // Strip path separators + control chars + cap at 200 chars so a
        // malicious filename can't drive the admin UI sideways.
        var clean = new string(raw.Where(c => c >= 0x20 && c != '/' && c != '\\' && c != '\0').ToArray());
        return clean.Length > 200 ? clean[..200] : clean;
    }
}
