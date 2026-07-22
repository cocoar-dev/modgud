using System.Text;
using BuildingBlocks.Helper;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Apps;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Domain.RealmSettings;
using Marten;

namespace Modgud.Api.Features.Admin;

/// <summary>
/// Per-realm and per-Application PageBuilder configuration (ADR-0001). Each SPA
/// page-slug (<c>login</c>, <c>logout</c>, <c>password-forgot</c>, …) owns a
/// library of named <see cref="PageVariant"/>s plus an active selection. The
/// schema of a variant is opaque to the backend — a JSON string the
/// <c>@cocoar/vue-page-builder</c> renderer interprets in the SPA.
///
/// <para>Stored on the tenant-DB <see cref="RealmSettings"/> singleton
/// (<see cref="RealmSettings.PageSlots"/>) and per-App
/// <see cref="ApplicationSettings.PageSlots"/>. Legacy single-schema data is
/// migrated in-place on first touch via <c>MigratePagesToSlots</c>.</para>
/// </summary>
public static class CustomizationPagesEndpoints
{
    /// <summary>Max-length on a stored schema. JSON page-trees are tiny; 256 KB
    /// is far more than needed and caps abuse via the admin endpoint.</summary>
    private const int MaxSchemaBytes = 256 * 1024;

    private const int MaxVariantNameLength = 80;

    /// <summary>Guard against unbounded variant libraries per slot.</summary>
    private const int MaxVariantsPerSlot = 50;

    public static WebApplication MapCustomizationPagesEndpoints(this WebApplication app, string path)
    {
        MapRealmPageEndpoints(app, path);
        MapApplicationPageEndpoints(app, path);
        return app;
    }

    // ─────────────────────────── Realm ───────────────────────────

    private static void MapRealmPageEndpoints(WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/customization/pages")
            .WithTags("Admin Customization Pages")
            .RequireAuthorization();

        // GET / — every slot's variant library + active selection. The schema
        // bodies are omitted (list view); fetch a single variant for editing.
        group.MapGet("", async (
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            if (doc is not null && doc.MigratePagesToSlots())
            {
                session.Store(doc);
                await session.SaveChangesAsync(ct);
            }

            var slots = (doc?.PageSlots ?? new())
                .Select(kv => new
                {
                    Slug = kv.Key,
                    kv.Value.ActiveVariantId,
                    Variants = kv.Value.Variants.Select(SummariseVariant).ToArray(),
                })
                .ToArray();
            return Results.Ok(new { Slots = slots });
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_ListPages");

        // GET /{slug} — one slot's variants + active selection.
        group.MapGet("{slug}", async (
            string slug,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var (doc, changed) = await LoadRealmMigrated(session, ct);
            if (changed) { session.Store(doc!); await session.SaveChangesAsync(ct); }

            var slot = doc?.PageSlots?.GetValueOrDefault(slug);
            return Results.Ok(new
            {
                Slug = slug,
                ActiveVariantId = slot?.ActiveVariantId,
                Variants = (slot?.Variants ?? new()).Select(SummariseVariant).ToArray(),
            });
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_GetPageSlot");

        // GET /{slug}/variants/{variantId} — a single variant incl. its schema.
        group.MapGet("{slug}/variants/{variantId}", async (
            string slug,
            string variantId,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var (doc, changed) = await LoadRealmMigrated(session, ct);
            if (changed) { session.Store(doc!); await session.SaveChangesAsync(ct); }

            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants
                .FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();
            return Results.Ok(new { variant.Id, variant.Name, variant.Schema });
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_GetPageVariant");

        // POST /{slug}/variants — create a named variant. Does NOT activate it;
        // activation is a separate settings decision (ADR-0001).
        group.MapPost("{slug}/variants", async (
            string slug,
            SaveVariantRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            if (ValidateVariant(body, out var err) is false) return Results.BadRequest(new { Message = err });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct)
                ?? new RealmSettings { Id = RealmSettings.SingletonId, CreatedAt = DateTimeOffset.UtcNow };
            doc.MigratePagesToSlots();
            doc.PageSlots ??= new Dictionary<string, RealmPageSlot>(StringComparer.Ordinal);
            var slot = doc.PageSlots.TryGetValue(slug, out var s) ? s : (doc.PageSlots[slug] = new RealmPageSlot());
            if (slot.Variants.Count >= MaxVariantsPerSlot)
                return Results.BadRequest(new { Message = $"Too many variants (max {MaxVariantsPerSlot})." });

            var variant = new PageVariant
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = body.Name!.Trim(),
                Schema = body.Schema!,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            slot.Variants.Add(variant);
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { variant.Id, variant.Name });
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_CreatePageVariant");

        // PUT /{slug}/variants/{variantId} — update a variant's name / schema.
        group.MapPut("{slug}/variants/{variantId}", async (
            string slug,
            string variantId,
            SaveVariantRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            if (ValidateVariant(body, out var err) is false) return Results.BadRequest(new { Message = err });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            doc?.MigratePagesToSlots();
            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();

            variant.Name = body.Name!.Trim();
            variant.Schema = body.Schema!;
            variant.UpdatedAt = DateTimeOffset.UtcNow;
            doc!.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { variant.Id, variant.Name });
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_UpdatePageVariant");

        // DELETE /{slug}/variants/{variantId} — remove a variant. If it was the
        // active one, the slot reverts to the built-in view.
        group.MapDelete("{slug}/variants/{variantId}", async (
            string slug,
            string variantId,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            doc?.MigratePagesToSlots();
            var slot = doc?.PageSlots?.GetValueOrDefault(slug);
            var removed = slot?.Variants.RemoveAll(v => v.Id == variantId) > 0;
            if (removed)
            {
                if (slot!.ActiveVariantId == variantId) slot.ActiveVariantId = null;
                doc!.UpdatedAt = DateTimeOffset.UtcNow;
                session.Store(doc);
                await session.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_DeletePageVariant");

        // PUT /{slug}/active — set which variant is live (null = built-in).
        group.MapPut("{slug}/active", async (
            string slug,
            SetRealmActiveRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct)
                ?? new RealmSettings { Id = RealmSettings.SingletonId, CreatedAt = DateTimeOffset.UtcNow };
            doc.MigratePagesToSlots();
            doc.PageSlots ??= new Dictionary<string, RealmPageSlot>(StringComparer.Ordinal);
            var slot = doc.PageSlots.TryGetValue(slug, out var s) ? s : (doc.PageSlots[slug] = new RealmPageSlot());

            if (body.ActiveVariantId is not null &&
                slot.Variants.All(v => v.Id != body.ActiveVariantId))
                return Results.BadRequest(new { Message = "No such variant for this slot." });

            slot.ActiveVariantId = body.ActiveVariantId;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { Slug = slug, slot.ActiveVariantId });
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_SetPageActive");
    }

    // ──────────────────────── Application ────────────────────────

    private static void MapApplicationPageEndpoints(WebApplication app, string path)
    {
        // App page config uses app:read/write — the pages are part of the App
        // resource, not the Realm settings resource.
        var group = app.MapGroup($"{path}/app/{{applicationId}}/pages")
            .WithTags("Admin Application Customization Pages")
            .RequireAuthorization();

        group.MapGet("", async (
            ShortGuid applicationId,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct);
            if (doc is not null && doc.MigratePagesToSlots())
            {
                session.Store(doc);
                await session.SaveChangesAsync(ct);
            }

            var slots = (doc?.PageSlots ?? new())
                .Select(kv => new
                {
                    Slug = kv.Key,
                    kv.Value.InheritActive,
                    kv.Value.ActiveVariantId,
                    Variants = kv.Value.Variants.Select(SummariseVariant).ToArray(),
                })
                .ToArray();
            return Results.Ok(new { Slots = slots });
        })
        .RequiresPermission("app:read")
        .WithName("Admin_ApplicationCustomization_ListPages");

        group.MapGet("{slug}/variants/{variantId}", async (
            ShortGuid applicationId,
            string slug,
            string variantId,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct);
            doc?.MigratePagesToSlots();
            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();
            return Results.Ok(new { variant.Id, variant.Name, variant.Schema });
        })
        .RequiresPermission("app:read")
        .WithName("Admin_ApplicationCustomization_GetPageVariant");

        group.MapPost("{slug}/variants", async (
            ShortGuid applicationId,
            string slug,
            SaveVariantRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            if (ValidateVariant(body, out var err) is false) return Results.BadRequest(new { Message = err });
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct)
                ?? new ApplicationSettings { Id = applicationId.Guid, CreatedAt = DateTimeOffset.UtcNow };
            doc.MigratePagesToSlots();
            doc.PageSlots ??= new Dictionary<string, AppPageSlot>(StringComparer.Ordinal);
            var slot = doc.PageSlots.TryGetValue(slug, out var s) ? s : (doc.PageSlots[slug] = new AppPageSlot());
            if (slot.Variants.Count >= MaxVariantsPerSlot)
                return Results.BadRequest(new { Message = $"Too many variants (max {MaxVariantsPerSlot})." });

            var variant = new PageVariant
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = body.Name!.Trim(),
                Schema = body.Schema!,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            slot.Variants.Add(variant);
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { variant.Id, variant.Name });
        })
        .RequiresPermission("app:write")
        .WithName("Admin_ApplicationCustomization_CreatePageVariant");

        group.MapPut("{slug}/variants/{variantId}", async (
            ShortGuid applicationId,
            string slug,
            string variantId,
            SaveVariantRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            if (ValidateVariant(body, out var err) is false) return Results.BadRequest(new { Message = err });
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct);
            doc?.MigratePagesToSlots();
            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();

            variant.Name = body.Name!.Trim();
            variant.Schema = body.Schema!;
            variant.UpdatedAt = DateTimeOffset.UtcNow;
            doc!.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { variant.Id, variant.Name });
        })
        .RequiresPermission("app:write")
        .WithName("Admin_ApplicationCustomization_UpdatePageVariant");

        group.MapDelete("{slug}/variants/{variantId}", async (
            ShortGuid applicationId,
            string slug,
            string variantId,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct);
            doc?.MigratePagesToSlots();
            var slot = doc?.PageSlots?.GetValueOrDefault(slug);
            var removed = slot?.Variants.RemoveAll(v => v.Id == variantId) > 0;
            if (removed)
            {
                if (slot!.ActiveVariantId == variantId) slot.ActiveVariantId = null;
                doc!.UpdatedAt = DateTimeOffset.UtcNow;
                session.Store(doc);
                await session.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        })
        .RequiresPermission("app:write")
        .WithName("Admin_ApplicationCustomization_DeletePageVariant");

        // PUT /{slug}/active — inherit the realm, or override (built-in / app variant).
        group.MapPut("{slug}/active", async (
            ShortGuid applicationId,
            string slug,
            SetAppActiveRequest body,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct)
                ?? new ApplicationSettings { Id = applicationId.Guid, CreatedAt = DateTimeOffset.UtcNow };
            doc.MigratePagesToSlots();
            doc.PageSlots ??= new Dictionary<string, AppPageSlot>(StringComparer.Ordinal);
            var slot = doc.PageSlots.TryGetValue(slug, out var s) ? s : (doc.PageSlots[slug] = new AppPageSlot());

            if (!body.Inherit && body.ActiveVariantId is not null &&
                slot.Variants.All(v => v.Id != body.ActiveVariantId))
                return Results.BadRequest(new { Message = "No such variant for this slot." });

            slot.InheritActive = body.Inherit;
            slot.ActiveVariantId = body.Inherit ? null : body.ActiveVariantId;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { Slug = slug, slot.InheritActive, slot.ActiveVariantId });
        })
        .RequiresPermission("app:write")
        .WithName("Admin_ApplicationCustomization_SetPageActive");
    }

    // ──────────────────────────── helpers ────────────────────────────

    private static async Task<(RealmSettings? doc, bool changed)> LoadRealmMigrated(
        IDocumentSession session, CancellationToken ct)
    {
        var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
        var changed = doc?.MigratePagesToSlots() ?? false;
        return (doc, changed);
    }

    private static object SummariseVariant(PageVariant v) => new
    {
        v.Id,
        v.Name,
        v.CreatedAt,
        v.UpdatedAt,
    };

    private static bool ValidateVariant(SaveVariantRequest body, out string error)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            error = "Name required.";
            return false;
        }
        if (body.Name.Trim().Length > MaxVariantNameLength)
        {
            error = $"Name too long (max {MaxVariantNameLength}).";
            return false;
        }
        if (body.Schema is null)
        {
            error = "Schema required.";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(body.Schema) > MaxSchemaBytes)
        {
            error = $"Schema too large (max {MaxSchemaBytes} bytes).";
            return false;
        }
        try { System.Text.Json.JsonDocument.Parse(body.Schema); }
        catch (System.Text.Json.JsonException ex)
        {
            error = $"Schema is not valid JSON: {ex.Message}";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>Allow lowercase ASCII + hyphens, length 1-32. Keeps URL-pretty
    /// and rejects anything that could route-inject.</summary>
    private static bool IsValidSlug(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 32) return false;
        foreach (var c in slug)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        }
        return true;
    }

    public record SaveVariantRequest(string? Name, string? Schema);
    public record SetRealmActiveRequest(string? ActiveVariantId);
    public record SetAppActiveRequest(bool Inherit, string? ActiveVariantId);
}
