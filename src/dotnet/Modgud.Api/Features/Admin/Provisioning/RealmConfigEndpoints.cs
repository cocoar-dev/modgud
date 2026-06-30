using ErrorOr;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Modgud.Authorization.AspNetCore;
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
            .WithTags("Realm Config")
            .RequireAuthorization()
            // realm:admin in the CURRENT realm (the modgud app's realm-wide bypass). Not the
            // control-plane app — this surface is the realm's own, not cross-realm.
            .RequiresPermission(PermissionEvaluator.RealmAdminPermission);

        // The manifest JSON Schema (identical to the control-plane one) so a realm admin / agent
        // can fetch the contract without control-plane access.
        group.MapGet("manifest-schema", (IOptions<JsonOptions> jsonOptions) =>
        {
            var schema = RealmManifestSchema.Build(jsonOptions.Value.SerializerOptions);
            return Results.Text(
                schema.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                "application/json");
        })
        .WithName("RealmConfig_ManifestSchema");

        // Export THIS realm's config as a structure-only manifest (never secrets / hashes).
        group.MapGet("export", async (RealmManifestExporter exporter, CancellationToken ct) =>
        {
            var result = await exporter.ExportRealmAsync(TenantContext.Current, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Export");

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
            var result = await applier.UpdateRealmAsync(scoped, prune, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("RealmConfig_Apply");

        return application;
    }

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
