using System.Security.Claims;
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
                var lastInvite = await session.Query<PendingAdminInvite>()
                    .OrderByDescending(i => i.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (lastInvite is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Realm.NoPriorInvite",
                        detail: "No prior bootstrap-invite for this realm — resend re-uses the original recipient identity.");
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

        group.MapDelete("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteRealmAsync(slug, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Realms_Delete")
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
