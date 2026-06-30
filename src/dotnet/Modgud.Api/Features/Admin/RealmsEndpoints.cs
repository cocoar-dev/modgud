using System.Security.Claims;
using ErrorOr;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Setup;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Services;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modgud.Api.Features.Admin;

/// <summary>
/// Realm management endpoints. Only callable from the Control-Plane realm
/// (<see cref="Realm.IsControlPlane"/>). Gated by <c>control-plane:realm:*</c>
/// permissions on top of the realm-level capability check.
///
/// <para>The <c>control-plane:*</c> permission namespace is intentionally
/// decoupled from the product slug <c>modgud</c> — if the IdP product
/// is ever rebranded, the global-admin permissions don't need a migration.</para>
/// </summary>
public static class RealmsEndpoints
{
    public static WebApplication MapRealmsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/realms")
            .WithTags("Realms")
            .RequireAuthorization()
            .AddEndpointFilter<RequireControlPlaneFilter>();

        group.MapGet("", async (IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var realms = await svc.GetAllRealmsAsync(ct);
            var items = realms.Select(MapToDto).ToList();
            return Results.Ok(new RealmListDto { Items = items, TotalCount = items.Count });
        })
        .WithName("Realms_List")
        .RequiresPermission("realm:read", AppSlugs.ControlPlane);

        group.MapGet("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var realm = await svc.GetRealmBySlugAsync(slug, ct);
            return realm is null ? Results.NotFound() : Results.Ok(MapToDto(realm));
        })
        .WithName("Realms_Get")
        .RequiresPermission("realm:read", AppSlugs.ControlPlane);

        group.MapPost("", async (
            CreateRealmDto dto,
            IRealmProvisioningService svc,
            IServiceProvider sp,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var result = await svc.CreateRealmAsync(dto, ct);
            if (result.IsError) return result.ToResult();

            ModgudMeters.RecordRealmProvisioned();
            var realm = result.Value;
            var issuedBy = http.User.Identity?.IsAuthenticated == true
                ? (http.User.FindFirstValue(ClaimTypes.Name) ?? http.User.Identity.Name)
                : null;

            // Issue the bootstrap-invite atomically with the realm. We hop
            // into the new tenant's context so the IPendingAdminInviteService
            // resolves a session against the just-provisioned tenant DB.
            // Living in the API layer (not Infrastructure) avoids the
            // Authentication ↔ Infrastructure circular reference.
            //
            // Atomicity: CreateRealmAsync already committed the realm + its
            // tenant DB. If issuing the invite throws, the operator would
            // otherwise be stranded — an adminless realm exists, a retry of
            // this call 409s, and recovery is filesystem-CLI-only. Compensate
            // by rolling the realm back so the create is all-or-nothing from
            // the caller's view and a retry is clean.
            IssuedInvite issued;
            try
            {
                using var inviteScope = sp.CreateScope();
                using (TenantContext.Enter(realm.Slug))
                {
                    var inviteService = inviteScope.ServiceProvider.GetRequiredService<IPendingAdminInviteService>();
                    issued = await inviteService.IssueAsync(
                        dto.InitialAdmin.UserName,
                        dto.InitialAdmin.Email,
                        dto.InitialAdmin.Firstname,
                        dto.InitialAdmin.Lastname,
                        issuedBy,
                        realm,
                        ct);
                }
            }
            catch (Exception ex)
            {
                var log = loggerFactory.CreateLogger("Modgud.Api.Features.Admin.RealmsEndpoints");
                log.LogError(ex,
                    "Bootstrap-invite issuance failed for realm {Slug}; rolling back the partially-provisioned realm.",
                    realm.Slug);

                await svc.RollbackProvisionedRealmAsync(realm.Slug, ct);

                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Realm.Provisioning.InviteFailed",
                    detail: $"Realm '{realm.Slug}' was provisioned but issuing the initial-admin invite failed. "
                          + "The partially-provisioned realm has been rolled back — it is safe to retry. "
                          + "See the server logs / the realm error feed for the underlying cause.");
            }

            return Results.Created(
                $"{path}/admin/realms/{realm.Slug}",
                new CreatedRealmDto
                {
                    Realm = MapToDto(realm),
                    InitialAdminInvite = new InitialAdminInviteDto
                    {
                        UserName = issued.UserName,
                        Email = issued.Email,
                        ExpiresAt = issued.ExpiresAt,
                        MagicLinkUrl = issued.MagicLinkUrl,
                    },
                });
        })
        .WithName("Realms_Create")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        // C15c — Resend bootstrap-invite. Re-uses the recipient identity
        // from the most recent prior invite in the tenant DB (typically
        // the one the realm was created with). The previous invite is
        // revoked inside IssueAsync; the new token has a fresh 7-day
        // expiry. Returns the magic-link URL just like Create does, for
        // SMTP-less dev visibility.
        group.MapPost("{slug}/resend-bootstrap-invite", async (
            string slug,
            IRealmProvisioningService svc,
            IServiceProvider sp,
            HttpContext http,
            CancellationToken ct) =>
        {
            var realm = await svc.GetRealmBySlugAsync(slug, ct);
            if (realm is null) return Results.NotFound();
            if (!realm.IsActive)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Realm.Inactive",
                    detail: $"Realm '{slug}' is inactive.");
            }

            var issuedBy = http.User.Identity?.IsAuthenticated == true
                ? (http.User.FindFirstValue(ClaimTypes.Name) ?? http.User.Identity.Name)
                : null;

            IssuedInvite issued;
            using (var inviteScope = sp.CreateScope())
            using (TenantContext.Enter(slug))
            {
                var session = inviteScope.ServiceProvider.GetRequiredService<IDocumentSession>();
                // Audit #32 — only an UNUSED invite may be resent. Without the
                // UsedAt==null filter, resend returns the most recent invite even
                // after it was consumed, re-arming a fresh 7-day realm:admin token
                // (and a misleading "invite issued" audit entry) for an already-
                // bootstrapped realm. With the filter a bootstrapped realm has no
                // pending invite to resend → 404.
                var lastInvite = await session.Query<PendingAdminInvite>()
                    .Where(i => i.UsedAt == null)
                    .OrderByDescending(i => i.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (lastInvite is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Realm.NoPriorInvite",
                        detail: "No pending bootstrap-invite for this realm — there is no unused invite to resend (it may already be bootstrapped).");
                }

                var inviteService = inviteScope.ServiceProvider.GetRequiredService<IPendingAdminInviteService>();
                issued = await inviteService.IssueAsync(
                    lastInvite.UserName,
                    lastInvite.Email,
                    lastInvite.Firstname,
                    lastInvite.Lastname,
                    issuedBy,
                    realm,
                    ct);
            }

            return Results.Ok(new InitialAdminInviteDto
            {
                UserName = issued.UserName,
                Email = issued.Email,
                ExpiresAt = issued.ExpiresAt,
                MagicLinkUrl = issued.MagicLinkUrl,
            });
        })
        .WithName("Realms_ResendBootstrapInvite")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        group.MapPatch("{slug}", async (
            string slug,
            UpdateRealmDto dto,
            IRealmProvisioningService svc,
            CancellationToken ct) =>
        {
            var result = await svc.UpdateRealmAsync(slug, dto, ct);
            return result.ToResult(realm => Results.Ok(MapToDto(realm)));
        })
        .WithName("Realms_Update")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        // ?hard=true escalates from the reversible soft-delete to the prod-safe hard
        // delete that DROPs the tenant database (HardDeleteRealmAsync). Default false keeps
        // the existing soft-delete behaviour. Hard-delete is refused for the control plane.
        group.MapDelete("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct, bool hard = false) =>
        {
            var result = hard
                ? await svc.HardDeleteRealmAsync(slug, ct)
                : await svc.DeleteRealmAsync(slug, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Realms_Delete")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        // ── Declarative provisioning (RealmManifestApplier) ─────────────────────────
        // Import a brand-new realm from a complete manifest (realm + settings + apps +
        // apis + scopes + clients + roles + users + groups), all via the canonical admin
        // operations. The slug must NOT already exist; a failed import rolls the whole
        // realm back (hard-delete). Returns the created slug + primary domain + the
        // plaintext secrets of any confidential clients (only available at create time).
        group.MapPost("import", async (
            RealmManifest manifest, RealmManifestApplier applier, CancellationToken ct) =>
        {
            var result = await applier.ImportNewRealmAsync(manifest, ct);
            if (result.IsError) return ManifestError(result.Errors);
            ModgudMeters.RecordRealmProvisioned();
            return Results.Created($"{path}/admin/realms/{result.Value.Slug}", result.Value);
        })
        .WithName("Realms_Import")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        // Apply a manifest to an EXISTING realm: in-place merge/upsert per entity (never
        // drops the DB). The route slug must match the manifest's realm slug. Default is an
        // additive merge (entities absent from the manifest are left untouched);
        // ?prune=true makes it a full sync that also deletes the absent entities (k8s
        // apply --prune — infrastructure + every realm:admin path are protected, never pruned).
        group.MapPost("{slug}/apply", async (
            string slug, RealmManifest manifest, RealmManifestApplier applier, CancellationToken ct, bool prune = false) =>
        {
            if (!string.Equals(slug, manifest.Realm.Slug, StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    Error = "Manifest.SlugMismatch",
                    Message = $"Route slug '{slug}' does not match the manifest realm slug '{manifest.Realm.Slug}'.",
                });

            var result = await applier.UpdateRealmAsync(manifest, prune, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("Realms_Apply")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        // Export a realm's current config as a manifest (structure-only — never secrets or
        // password hashes). Round-trips with /apply: GET, edit (e.g. add a user password),
        // POST back to /{slug}/apply.
        group.MapGet("{slug}/export", async (
            string slug, RealmManifestExporter exporter, CancellationToken ct) =>
        {
            var result = await exporter.ExportRealmAsync(slug, ct);
            return result.IsError ? ManifestError(result.Errors) : Results.Ok(result.Value);
        })
        .WithName("Realms_Export")
        .RequiresPermission("realm:read", AppSlugs.ControlPlane);

        // The JSON Schema for the import/apply body, generated from the live RealmManifest type
        // (so it can't drift from the contract) with per-field descriptions + a worked example.
        // Lets a consumer / agent fetch the contract and author a valid manifest without the
        // source. Generated with the API's own JSON options so property casing matches the wire.
        // Gated with the SAME permission as import/apply (realm:write) — only a caller who can
        // actually apply a manifest may fetch its schema.
        group.MapGet("manifest-schema", (
            Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) =>
        {
            var schema = Provisioning.RealmManifestSchema.Build(jsonOptions.Value.SerializerOptions);
            return Results.Text(
                schema.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                "application/json");
        })
        .WithName("Realms_ManifestSchema")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        // Transfer the control-plane role to {slug}. POST to the realm that
        // should BECOME the control plane, from the current control-plane host
        // (the group's RequireControlPlaneFilter enforces the latter). After
        // the move the OLD host loses this surface (its realm is no longer the
        // control plane) and the NEW host's realm:admins gain it — authority is
        // realm:admin within whichever realm holds the flag, so no permission
        // migration is needed.
        group.MapPost("{slug}/transfer-control-plane", async (
            string slug,
            IRealmProvisioningService svc,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var target = await svc.GetRealmBySlugAsync(slug, ct);
            if (target is null) return Results.NotFound();

            // Fail-closed lockout guard (the operator break-glass CLI bypasses
            // this by calling the service directly): refuse an in-app transfer
            // to an ACTIVE realm that has no usable cross-realm admin. Without
            // it, a single UI/API action would move the control plane off this
            // host (which then 404s realm management) onto a realm where nobody
            // can pass control-plane:realm:write — bricking web-based admin with
            // only a filesystem/CLI recovery left. (Inactive targets fall through
            // to the service's TargetInactive guard.)
            if (target.IsActive && !await TargetHasUsableAdminAsync(sp, slug, ct))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Realm.TargetHasNoAdmin",
                    detail: $"Realm '{slug}' has no active admin (realm:admin) — transferring the control plane to it would lock everyone out of realm management. Bootstrap an admin in '{slug}' first, or use the recovery CLI (which bypasses this check).");
            }

            var result = await svc.TransferControlPlaneAsync(slug, ct);
            return result.ToResult(realm => Results.Ok(MapToDto(realm)));
        })
        .WithName("Realms_TransferControlPlane")
        .RequiresPermission("realm:write", AppSlugs.ControlPlane);

        return application;
    }

    /// <summary>
    /// True when <paramref name="targetSlug"/> has at least one active,
    /// non-deleted user with effective control-plane authority
    /// (<c>control-plane:realm:write</c> — i.e. a realm:admin or a scoped
    /// grant). The fail-closed pre-transfer guard so a UI/API transfer can't
    /// strand the deployment with no one able to manage realms. Enters the
    /// target realm's tenant context so the permission check resolves against
    /// its own DB.
    /// </summary>
    private static async Task<bool> TargetHasUsableAdminAsync(
        IServiceProvider sp, string targetSlug, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        using (TenantContext.Enter(targetSlug))
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

            var users = await session.Query<ApplicationUser>()
                .Where(u => u.IsActive && !u.IsDeleted)
                .ToListAsync(ct);

            foreach (var user in users)
            {
                if (await permissions.HasPermissionAsync(user.Id, AppSlugs.ControlPlane, "realm:write", ct))
                    return true;
            }
            return false;
        }
    }

    // Renders a RealmManifestApplier ErrorOr error with the code in the body — the manifest
    // codes (Realm.AlreadyExists / Realm.NotFound / Manifest.*) are how a test-kit / caller
    // distinguishes outcomes, so don't collapse them through the shared ToResult.
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

    internal static RealmDto MapToDto(Realm realm) => new()
    {
        Id = realm.Id,
        Slug = realm.Slug,
        DisplayName = realm.DisplayName,
        Description = realm.Description,
        Domains = realm.Domains,
        PrimaryDomain = realm.PrimaryDomain,
        IsControlPlane = realm.IsControlPlane,
        IsActive = realm.IsActive,
        NeedsSetup = false, // per-realm setup detection comes in a later etappe
        CreatedAt = realm.CreatedAt,
    };
}

// RequireControlPlaneFilter lives in Modgud.Infrastructure.Realms so
// auth-slice endpoints can apply the same filter without circular refs
// without an Api ↔ Auth circular reference (C14b).
