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
/// PageBuilder configuration (ADR-0013). The variant library is <b>realm-global</b>:
/// each SPA page-slug (<c>login</c>, <c>logout</c>, <c>password-forgot</c>,
/// <c>consent</c>) owns a
/// set of named <see cref="PageVariant"/>s on the tenant <see cref="RealmSettings"/>.
/// The realm picks which variant is active for itself; each Application only
/// *selects* one of those realm variants (or inherits / built-in). Schemas are
/// PageBuilder JSON document that is validated before it can be published.
/// </summary>
public static class CustomizationPagesEndpoints
{
    private const int MaxSchemaBytes = 256 * 1024;
    private const int MaxVariantNameLength = 80;
    private const int MaxVariantsPerSlot = 50;
    private const int MaxRevisionsPerVariant = 100;

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

        // GET / — every slot's variant library + active selection + usage.
        group.MapGet("", async (
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();

            var (doc, changed) = await LoadRealmMigrated(session, ct);
            if (changed) { session.Store(doc!); await session.SaveChangesAsync(ct); }

            var usage = await ComputeAppUsage(session, ct);
            var slots = (doc?.PageSlots ?? new())
                .Select(kv => new
                {
                    Slug = kv.Key,
                    kv.Value.ActiveVariantId,
                    Variants = kv.Value.Variants
                        .Select(v => SummariseVariant(v, kv.Key, kv.Value.ActiveVariantId, usage))
                        .ToArray(),
                })
                .ToArray();
            return Results.Ok(new { Slots = slots });
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_ListPages");

        // GET /{slug} — one slot's variants + active selection + usage.
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
            var usage = await ComputeAppUsage(session, ct);
            return Results.Ok(new
            {
                Slug = slug,
                ActiveVariantId = slot?.ActiveVariantId,
                Variants = (slot?.Variants ?? new())
                    .Select(v => SummariseVariant(v, slug, slot?.ActiveVariantId, usage))
                    .ToArray(),
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

            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();
            return Results.Ok(new
            {
                variant.Id,
                variant.Name,
                variant.Schema,
                variant.PublishedRevision,
                variant.PublishedAt,
                IsPublished = variant.PublishedSchema is not null,
                HasUnpublishedChanges = (variant.PublishedAuthoringSchema ?? variant.PublishedSchema) != variant.Schema,
                Revisions = (variant.Revisions ?? new List<PageVariantRevision>())
                    .OrderByDescending(r => r.Number)
                    .Select(r => new { r.Number, r.PublishedAt, r.PublishedBy, r.RollbackOfRevision })
                    .ToArray(),
            });
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_GetPageVariant");

        // POST /{slug}/variants — create a named variant (does not activate it).
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
            var slot = doc?.PageSlots?.GetValueOrDefault(slug);
            var variant = slot?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();

            // First edit of an active legacy document snapshots the previous
            // live schema before replacing its draft.
            if (slot!.ActiveVariantId == variantId && variant.PublishedSchema is null)
            {
                if (!PageCompositionDocumentService.ValidateAndCompilePage(
                        slug, variant.Schema, doc?.PageCompositions ?? [], out var legacyRuntime, out var legacyError))
                    return Results.BadRequest(new { Message = legacyError });
                PublishDraft(variant, legacyRuntime, publishedBy: null);
            }

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

        // POST /{slug}/variants/{variantId}/publish — validate the draft at
        // the server boundary, append an immutable revision and atomically
        // promote it. Existing realm/App selections then see the new revision.
        group.MapPost("{slug}/variants/{variantId}/publish", async (
            string slug,
            string variantId,
            HttpContext http,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            doc?.MigratePagesToSlots();
            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();
            if (!PageCompositionDocumentService.ValidateAndCompilePage(
                    slug, variant.Schema, doc?.PageCompositions ?? [], out var runtimeSchema, out var validationError))
                return Results.BadRequest(new { Message = validationError });

            PublishDraft(variant, runtimeSchema, http.User.Identity?.Name);
            doc!.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { variant.Id, variant.PublishedRevision, variant.PublishedAt });
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_PublishPageVariant");

        // POST /{slug}/variants/{variantId}/rollback/{revision} — rollback is
        // itself a new auditable publication; history is never rewritten.
        group.MapPost("{slug}/variants/{variantId}/rollback/{revision:int}", async (
            string slug,
            string variantId,
            int revision,
            HttpContext http,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!IsValidSlug(slug)) return Results.BadRequest(new { Message = "Invalid slug." });

            var doc = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            doc?.MigratePagesToSlots();
            var variant = doc?.PageSlots?.GetValueOrDefault(slug)?.Variants.FirstOrDefault(v => v.Id == variantId);
            if (variant is null) return Results.NotFound();
            var target = variant.Revisions?.FirstOrDefault(r => r.Number == revision);
            if (target is null) return Results.NotFound();
            if (!PageCompositionDocumentService.ValidateAndCompilePage(
                    slug, target.Schema, doc?.PageCompositions ?? [], out var runtimeSchema, out var validationError))
                return Results.BadRequest(new { Message = validationError });

            variant.Schema = target.Schema;
            PublishDraft(variant, runtimeSchema, http.User.Identity?.Name, rollbackOfRevision: revision);
            variant.UpdatedAt = DateTimeOffset.UtcNow;
            doc!.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(doc);
            await session.SaveChangesAsync(ct);
            return Results.Ok(new { variant.Id, variant.PublishedRevision, variant.PublishedAt });
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_RollbackPageVariant");

        // DELETE /{slug}/variants/{variantId} — remove a variant. Clears the
        // realm active pointer if it targeted this variant; Application selections
        // that pointed here fall back to built-in at resolution time.
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

        // PUT /{slug}/active — set which realm variant is live (null = built-in).
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

            if (body.ActiveVariantId is not null)
            {
                var variant = slot.Variants.FirstOrDefault(v => v.Id == body.ActiveVariantId);
                if (variant is null)
                    return Results.BadRequest(new { Message = "No such variant for this slot." });
                if (!PageCompositionDocumentService.ValidateAndCompilePage(
                        slug, variant.Schema, doc.PageCompositions ?? [], out var runtimeSchema, out var validationError))
                    return Results.BadRequest(new { Message = validationError });
                PublishDraft(variant, runtimeSchema, publishedBy: null);
            }

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
        var group = app.MapGroup($"{path}/app/{{applicationId}}/pages")
            .WithTags("Admin Application Customization Pages")
            .RequireAuthorization();

        // GET / — the App's per-slot selection plus the realm variants it can
        // choose from (Applications do not author their own variants).
        group.MapGet("", async (
            ShortGuid applicationId,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            var owningApp = await session.LoadAsync<App>(applicationId.Guid, ct);
            if (owningApp is null || owningApp.IsDeleted) return Results.NotFound();

            var appDoc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct);
            if (appDoc is not null && appDoc.MigratePagesToSlots())
            {
                session.Store(appDoc);
                await session.SaveChangesAsync(ct);
            }

            var (realm, realmChanged) = await LoadRealmMigrated(session, ct);
            if (realmChanged) { session.Store(realm!); await session.SaveChangesAsync(ct); }

            var slots = (realm?.PageSlots ?? new()).Keys
                .Union(appDoc?.PageSlots?.Keys ?? Enumerable.Empty<string>())
                .Distinct()
                .Select(slug =>
                {
                    var appSlot = appDoc?.PageSlots?.GetValueOrDefault(slug);
                    var realmSlot = realm?.PageSlots?.GetValueOrDefault(slug);
                    var realmVariants = realmSlot?.Variants ?? new();
                    return new
                    {
                        Slug = slug,
                        InheritActive = appSlot?.InheritActive ?? true,
                        ActiveVariantId = appSlot?.ActiveVariantId,
                        AvailableVariants = realmVariants
                            .Where(v => v.PublishedSchema is not null || realmSlot?.ActiveVariantId == v.Id)
                            .Select(v => new { v.Id, v.Name })
                            .ToArray(),
                    };
                })
                .ToArray();
            return Results.Ok(new { Slots = slots });
        })
        .RequiresPermission("app:read")
        .WithName("Admin_ApplicationCustomization_ListPages");

        // PUT /{slug}/active — inherit the realm, or override to built-in / a
        // realm variant. The App selects from the realm library, never its own.
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

            if (!body.Inherit && body.ActiveVariantId is not null)
            {
                var realm = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
                realm?.MigratePagesToSlots();
                var realmVariants = realm?.PageSlots?.GetValueOrDefault(slug)?.Variants;
                if (realmVariants is null || realmVariants.All(v =>
                        v.Id != body.ActiveVariantId || v.PublishedSchema is null))
                    return Results.BadRequest(new { Message = "No such realm variant for this slot." });
            }

            var doc = await session.LoadAsync<ApplicationSettings>(applicationId.Guid, ct)
                ?? new ApplicationSettings { Id = applicationId.Guid, CreatedAt = DateTimeOffset.UtcNow };
            doc.MigratePagesToSlots();
            doc.PageSlots ??= new Dictionary<string, AppPageSlot>(StringComparer.Ordinal);
            var slot = doc.PageSlots.TryGetValue(slug, out var s) ? s : (doc.PageSlots[slug] = new AppPageSlot());

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

    /// <summary>slug → variantId → Application display-names that activate it
    /// (non-inheriting). Drives the "Used By" column on the realm grid.</summary>
    private static async Task<Dictionary<string, Dictionary<string, List<string>>>> ComputeAppUsage(
        IDocumentSession session, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var appSettings = await session.Query<ApplicationSettings>().ToListAsync(ct);
        if (appSettings.Count == 0) return result;

        var referenced = appSettings.Where(a => a.PageSlots is not null).Select(a => a.Id).ToArray();
        if (referenced.Length == 0) return result;
        var apps = await session.Query<App>().Where(a => referenced.Contains(a.Id) && !a.IsDeleted).ToListAsync(ct);
        var nameById = apps.ToDictionary(a => a.Id, a => a.DisplayName);

        foreach (var s in appSettings)
        {
            if (s.PageSlots is null || !nameById.TryGetValue(s.Id, out var name)) continue;
            foreach (var (slug, slot) in s.PageSlots)
            {
                if (slot.InheritActive || slot.ActiveVariantId is null) continue;
                var perSlug = result.TryGetValue(slug, out var m) ? m : (result[slug] = new(StringComparer.Ordinal));
                var list = perSlug.TryGetValue(slot.ActiveVariantId, out var l) ? l : (perSlug[slot.ActiveVariantId] = new());
                list.Add(name);
            }
        }
        return result;
    }

    private static object SummariseVariant(
        PageVariant v,
        string slug,
        string? realmActiveId,
        Dictionary<string, Dictionary<string, List<string>>> usage)
    {
        var apps = usage.TryGetValue(slug, out var m) && m.TryGetValue(v.Id, out var list)
            ? list
            : new List<string>();
        return new
        {
            v.Id,
            v.Name,
            v.CreatedAt,
            v.UpdatedAt,
            v.PublishedAt,
            v.PublishedRevision,
            IsPublished = v.PublishedSchema is not null,
            HasUnpublishedChanges = (v.PublishedAuthoringSchema ?? v.PublishedSchema) != v.Schema,
            RealmActive = realmActiveId == v.Id,
            UsedByApps = apps.ToArray(),
        };
    }

    private static bool ValidateVariant(SaveVariantRequest body, out string error)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) { error = "Name required."; return false; }
        if (body.Name.Trim().Length > MaxVariantNameLength) { error = $"Name too long (max {MaxVariantNameLength})."; return false; }
        if (body.Schema is null) { error = "Schema required."; return false; }
        if (Encoding.UTF8.GetByteCount(body.Schema) > MaxSchemaBytes) { error = $"Schema too large (max {MaxSchemaBytes} bytes)."; return false; }
        try { System.Text.Json.JsonDocument.Parse(body.Schema); }
        catch (System.Text.Json.JsonException ex) { error = $"Schema is not valid JSON: {ex.Message}"; return false; }
        error = string.Empty;
        return true;
    }

    private static void PublishDraft(
        PageVariant variant,
        string runtimeSchema,
        string? publishedBy,
        int? rollbackOfRevision = null)
    {
        if (rollbackOfRevision is null
            && (variant.PublishedAuthoringSchema ?? variant.PublishedSchema) == variant.Schema)
            return;
        variant.Revisions ??= new List<PageVariantRevision>();
        var now = DateTimeOffset.UtcNow;
        var number = Math.Max(
            variant.PublishedRevision,
            variant.Revisions.Count == 0 ? 0 : variant.Revisions.Max(r => r.Number)) + 1;
        variant.PublishedSchema = runtimeSchema;
        variant.PublishedAuthoringSchema = variant.Schema;
        variant.PublishedRevision = number;
        variant.PublishedAt = now;
        variant.Revisions.Add(new PageVariantRevision
        {
            Number = number,
            Schema = variant.Schema,
            PublishedAt = now,
            PublishedBy = publishedBy,
            RollbackOfRevision = rollbackOfRevision,
        });
        if (variant.Revisions.Count > MaxRevisionsPerVariant)
        {
            variant.Revisions.RemoveRange(0, variant.Revisions.Count - MaxRevisionsPerVariant);
        }
    }

    /// <summary>Allow lowercase ASCII + hyphens, length 1-32.</summary>
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
