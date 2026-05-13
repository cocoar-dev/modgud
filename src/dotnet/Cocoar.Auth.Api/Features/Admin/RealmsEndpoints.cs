using System.Security.Claims;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Setup;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Observability;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Api.Features.Admin;

/// <summary>
/// Realm management endpoints. Only callable from the Control-Plane realm
/// (<see cref="Realm.IsControlPlane"/>). Gated by <c>control-plane:realm:*</c>
/// permissions on top of the realm-level capability check.
///
/// <para>The <c>control-plane:*</c> permission namespace is intentionally
/// decoupled from the product slug <c>cocoar-auth</c> — if the IdP product
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
            CancellationToken ct) =>
        {
            var result = await svc.CreateRealmAsync(dto, ct);
            if (result.IsError) return result.ToResult();

            CocoarAuthMeters.RecordRealmProvisioned();
            var realm = result.Value;
            var issuedBy = http.User.Identity?.IsAuthenticated == true
                ? (http.User.FindFirstValue(ClaimTypes.Name) ?? http.User.Identity.Name)
                : null;

            // Issue the bootstrap-invite atomically with the realm. We hop
            // into the new tenant's context so the IPendingAdminInviteService
            // resolves a session against the just-provisioned tenant DB.
            // Living in the API layer (not Infrastructure) avoids the
            // Authentication ↔ Infrastructure circular reference.
            IssuedInvite issued;
            using (var inviteScope = sp.CreateScope())
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

        return application;
    }

    internal static RealmDto MapToDto(Realm realm) => new()
    {
        Id = realm.Id,
        Slug = realm.Slug,
        DisplayName = realm.DisplayName,
        Description = realm.Description,
        Domains = realm.Domains,
        IsControlPlane = realm.IsControlPlane,
        IsActive = realm.IsActive,
        NeedsSetup = false, // per-realm setup detection comes in a later etappe
        CreatedAt = realm.CreatedAt,
    };
}

// RequireControlPlaneFilter lives in Cocoar.Auth.Infrastructure.Realms so
// auth-slice endpoints can apply the same filter without circular refs
// without an Api ↔ Auth circular reference (C14b).
