using System.Security.Claims;
using ErrorOr;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Modgud.Api.Features.Management;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Permissions;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Per-realm (data-plane) declarative config — lets a <c>realm:admin</c> manage THEIR OWN
/// realm from a manifest (export → edit → apply, with optional prune), reusing the same
/// <see cref="RealmManifestApplier"/> / <see cref="RealmManifestExporter"/> as the
/// control-plane provisioning.
///
/// <para>The difference is the scope + the gate. These endpoints are NOT control-plane: they
/// run on whatever realm the request is host-routed to (<see cref="TenantContext.Current"/>)
/// and require <c>realm:admin</c> in THAT realm. So a delegated per-realm credential
/// (a service account or user holding realm:admin in one realm) can fully manage that realm's
/// config + entities, but CANNOT create or delete realms (those stay control-plane-only) and
/// cannot touch any other realm (tenant isolation + the slug guard below). Prune is allowed,
/// but only within the realm and with the same lockout/infra protections as the control-plane
/// path (system app, standard scopes, SA clients, and every realm:admin path are never pruned).</para>
/// </summary>
public static class RealmConfigEndpoints
{
    public static WebApplication MapRealmConfigEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/realm-config")
            .WithTags("Realm Config");

        // The manifest JSON Schema (identical to the control-plane one) so a realm admin / agent
        // can fetch the contract without control-plane access.
        group.MapGet("manifest-schema", (IOptions<JsonOptions> jsonOptions) =>
        {
            var schema = RealmManifestSchema.Build(jsonOptions.Value.SerializerOptions);
            return Results.Text(
                schema.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                "application/json");
        })
        .WithName("RealmConfig_ManifestSchema")
        // Explicit dual-mode gate: realm-admin cookie or a Management API
        // bearer whose Person/ServiceAccount holds realm:admin live.
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Export THIS realm's config as a structure-only manifest (never secrets / hashes).
        group.MapGet("export", async (RealmManifestExporter exporter, CancellationToken ct) =>
        {
            var result = await exporter.ExportRealmAsync(TenantContext.Current, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Export")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Dry-run: what WOULD an apply of this manifest change? Same slug guard as apply,
        // same ?prune= semantics (adds delete candidates + protections) — writes nothing.
        group.MapPost("plan", async (
            RealmManifest manifest, RealmManifestPlanner planner, CancellationToken ct, bool prune = false) =>
        {
            var currentSlug = TenantContext.Current;
            if (SlugMismatch(manifest, currentSlug) is { } mismatch) return mismatch;
            var scoped = manifest with { Realm = manifest.Realm with { Slug = currentSlug } };
            var result = await planner.PlanAsync(scoped, prune, baseline: null, deletions: null, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Plan")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        MapDraftEndpoints(group);

        // Apply a manifest to THIS realm: in-place merge/upsert. ?prune=true makes it a full
        // sync (deletes entities absent from the manifest) — bounded to this realm, protections
        // as on the control-plane path. Never drops the realm database.
        group.MapPost("apply", async (
            RealmManifest manifest, RealmManifestApplier applier, CancellationToken ct, bool prune = false) =>
        {
            var currentSlug = TenantContext.Current;

            // A realm admin may only manage their OWN realm. A manifest aimed at a different
            // slug is refused — this is the data-plane safety boundary (cross-realm writes and
            // realm lifecycle stay control-plane-only).
            if (!string.IsNullOrEmpty(manifest.Realm.Slug) &&
                !string.Equals(manifest.Realm.Slug, currentSlug, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    Error = "Manifest.SlugMismatch",
                    Message = $"This realm is '{currentSlug}'. A realm admin can only manage their own realm; the manifest targets '{manifest.Realm.Slug}'.",
                });
            }

            // Pin the manifest to the current realm (covers an empty slug in the body). The
            // realm shell (domains/display name) is not mutated by apply — only in-realm config.
            var scoped = manifest with { Realm = manifest.Realm with { Slug = currentSlug } };
            var result = await applier.UpdateRealmAsync(scoped, prune, deletions: null, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Apply")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        return application;
    }

    /// <summary>
    /// Named server-side drafts (ADR-0017 Phase 1): the staging documents behind the
    /// draft workspace. Same realm scoping and realm:admin gate as the rest of the
    /// surface; visibility (private vs shared) is enforced in <see cref="RealmDraftService"/>.
    /// </summary>
    private static void MapDraftEndpoints(IEndpointRouteBuilder group)
    {
        var drafts = group.MapGroup("drafts");

        drafts.MapGet("", async (HttpContext http, RealmDraftService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(RequireUserId(http), ct)))
        .WithName("RealmConfig_Drafts_List")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapPost("", async (
            CreateRealmDraftDto dto, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            if (dto.Manifest is not null && SlugMismatch(dto.Manifest, TenantContext.Current) is { } mismatch)
                return mismatch;
            var result = await service.CreateAsync(
                dto, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_Create")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapGet("{id:guid}", async (
            Guid id, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.GetAsync(id, RequireUserId(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_Get")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapPut("{id:guid}", async (
            Guid id, UpdateRealmDraftDto dto, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            if (dto.Manifest is not null && SlugMismatch(dto.Manifest, TenantContext.Current) is { } mismatch)
                return mismatch;
            var result = await service.UpdateAsync(
                id, dto, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_Update")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapDelete("{id:guid}", async (
            Guid id, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(id, RequireUserId(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.NoContent();
        })
        .WithName("RealmConfig_Drafts_Delete")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Un-stages one write-only secret (slot paths like "users/<key>/Password").
        drafts.MapDelete("{id:guid}/secret", async (
            Guid id, string slot, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.ClearSecretAsync(id, slot, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_ClearSecret")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // ── Active draft (implicit branches): the admin's checkout. ─────────────────
        drafts.MapGet("active", async (HttpContext http, RealmDraftService service, CancellationToken ct) =>
            Results.Ok(await service.GetActiveAsync(RequireUserId(http), ct)))
        .WithName("RealmConfig_Drafts_Active")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapPost("active/park", async (HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            await service.ParkAsync(RequireUserId(http), ct);
            return Results.NoContent();
        })
        .WithName("RealmConfig_Drafts_Park")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapPost("active/switch/{id:guid}", async (
            Guid id, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.SwitchAsync(id, RequireUserId(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_Switch")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // The staging seam: upserts one entity ("commit") into the active draft —
        // implicitly creating an auto-named draft when none is active. Body = the
        // manifest entity; the natural key is computed server-side.
        drafts.MapPut("active/entities/{section}", async (
            string section, System.Text.Json.Nodes.JsonObject entity,
            HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.StageEntityAsync(
                section, entity, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_StageEntity")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapDelete("active/entities/{section}", async (
            string section, string key, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.UnstageEntityAsync(
                section, key, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_UnstageEntity")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Staged deletes (ADR-0017): PUT stages the deletion of one LIVE entity —
        // implicitly creating an auto-named draft when none is active; DELETE undoes
        // it (the entity is restored from the draft's baseline).
        drafts.MapPut("active/deletions/{section}", async (
            string section, string key, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.StageDeleteAsync(
                section, key, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_StageDelete")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        drafts.MapDelete("active/deletions/{section}", async (
            string section, string key, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.UnstageDeleteAsync(
                section, key, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_UnstageDelete")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Rebase = "keep mine": baseline := current export, remaining differences
        // become intentional staged changes and stop flagging as conflicts.
        drafts.MapPost("{id:guid}/rebase", async (
            Guid id, HttpContext http, RealmDraftService service, CancellationToken ct) =>
        {
            var result = await service.RebaseAsync(
                id, TenantContext.Current, RequireUserId(http), UserName(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_Rebase")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Plan with the draft's baseline: the response carries three-way conflicts.
        drafts.MapPost("{id:guid}/plan", async (
            Guid id, HttpContext http, RealmDraftService service, CancellationToken ct, bool prune = false) =>
        {
            var result = await service.PlanAsync(id, prune, RequireUserId(http), ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Drafts_Plan")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);

        // Apply gate (ADR-0017): pre-validated by a fresh plan; refused with 409 while
        // it reports apply-errors or unresolved conflicts. Consumes the draft on success.
        drafts.MapPost("{id:guid}/apply", async (
            Guid id, HttpContext http, RealmDraftService service, CancellationToken ct, bool prune = false) =>
        {
            var result = await service.ApplyAsync(id, prune, RequireUserId(http), ct);
            if (result.IsError) return ManifestError(result.Errors);
            return result.Value.Refused
                ? Results.Json(new
                {
                    Error = "Draft.ApplyRefused",
                    Message = "The plan reports apply errors or unresolved conflicts — resolve them and re-plan first.",
                    Plan = result.Value.Plan,
                }, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(result.Value.Result);
        })
        .WithName("RealmConfig_Drafts_Apply")
        .RequiresManagementPermission(PermissionEvaluator.RealmAdminPermission);
    }

    /// <summary>The data-plane safety boundary shared by apply/plan/drafts: a manifest
    /// aimed at a different realm slug is refused.</summary>
    private static IResult? SlugMismatch(RealmManifest manifest, string currentSlug)
    {
        if (string.IsNullOrEmpty(manifest.Realm.Slug) ||
            string.Equals(manifest.Realm.Slug, currentSlug, StringComparison.Ordinal))
            return null;
        return Results.BadRequest(new
        {
            Error = "Manifest.SlugMismatch",
            Message = $"This realm is '{currentSlug}'. A realm admin can only manage their own realm; the manifest targets '{manifest.Realm.Slug}'.",
        });
    }

    private static Guid RequireUserId(HttpContext http)
        => http.GetUserId() ?? throw new InvalidOperationException(
            "Realm-config draft endpoints require an authenticated user principal.");

    private static string UserName(HttpContext http)
        => http.User.FindFirstValue(ClaimTypes.Name) ?? http.User.Identity?.Name ?? "unknown";

    // Renders a manifest ErrorOr with the error code in the body (mirrors RealmsEndpoints).
    private static IResult ManifestError(List<Error> errors)
    {
        var error = errors[0];
        var status = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Json(new { Error = error.Code, Message = error.Description }, statusCode: status);
    }
}
