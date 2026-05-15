using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Domain.RealmSettings;
using Marten;

namespace Cocoar.Auth.Api.Features.Admin;

/// <summary>
/// Per-realm page-builder schemas. One slot per SPA page-slug
/// (<c>login</c>, <c>logout</c>, <c>password-forgot</c>, …). The schema
/// is opaque to the backend — a JSON string that the
/// <see cref="@cocoar/vue-page-builder"/> renderer interprets at runtime
/// in the SPA.
///
/// <para>Stored as a dictionary on the tenant-DB <c>RealmSettings</c>
/// singleton (alongside Branding / SelfRegistration / Dcr). No schema
/// migration when new page-slots get added — the dictionary grows
/// implicitly.</para>
/// </summary>
public static class CustomizationPagesEndpoints
{
    /// <summary>Max-length on the stored schema. JSON page-trees are tiny
    /// (a handful of nodes); 256 KB is way more than needed and caps
    /// abuse via the admin endpoint.</summary>
    private const int MaxSchemaBytes = 256 * 1024;

    public static WebApplication MapCustomizationPagesEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/customization/pages")
            .WithTags("Admin Customization Pages")
            .RequireAuthorization();

        // GET /{slug} — returns the schema (or empty when never saved).
        // Gated by AppSettings.Features.PageBuilder: while off the entire
        // surface returns 404 so the SPA + curl-callers see "no such
        // endpoint" and the editor stays dark.
        group.MapGet("{slug}", async (
            string slug,
            AppSettings settings,
            IQuerySession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            var schema = doc?.Pages?.TryGetValue(slug, out var s) == true ? s : null;
            return Results.Ok(new { Slug = slug, Schema = schema });
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_GetPage");

        // PUT /{slug} — replaces the schema for that slug. Body is the
        // raw JSON schema as a string (wrapped in { Schema: "..." }).
        group.MapPut("{slug}", async (
            string slug,
            UpdatePageRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            if (body.Schema is null) return Results.BadRequest(new { Message = "Schema required." });
            if (body.Schema.Length > MaxSchemaBytes)
                return Results.BadRequest(new { Message = $"Schema too large (max {MaxSchemaBytes} bytes)." });

            // Validate it parses as JSON — anything else is a developer
            // bug or someone bypassing the editor. We don't try to
            // semantically validate the PageNode tree here; the SPA's
            // builder + renderer are the schema-shape authority.
            try { System.Text.Json.JsonDocument.Parse(body.Schema); }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { Message = $"Schema is not valid JSON: {ex.Message}" });
            }

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct)
                ?? new RealmSettings { Id = RealmSettings.SingletonId, CreatedAt = DateTimeOffset.UtcNow };
            doc.Pages ??= new Dictionary<string, string>();
            doc.Pages[slug] = body.Schema;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);

            return Results.Ok(new { Slug = slug, Schema = body.Schema });
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_PutPage");

        // DELETE /{slug} — drops the slug, SPA reverts to its hardcoded
        // view for that page.
        group.MapDelete("{slug}", async (
            string slug,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            if (doc?.Pages?.Remove(slug) == true)
            {
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                session.Store(doc);
                await session.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_DeletePage");

        return app;
    }

    /// <summary>Allow lowercase ASCII + hyphens, length 1-32. Keeps URL-
    /// pretty and rejects anything that could route-injection.</summary>
    private static bool IsValidSlug(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 32) return false;
        foreach (var c in slug)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        }
        return true;
    }

    public record UpdatePageRequest(string? Schema);
}
