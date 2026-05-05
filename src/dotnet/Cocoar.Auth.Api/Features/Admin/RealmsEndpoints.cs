using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;

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
        .RequiresPermission("control-plane:realm:read");

        group.MapGet("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var realm = await svc.GetRealmBySlugAsync(slug, ct);
            return realm is null ? Results.NotFound() : Results.Ok(MapToDto(realm));
        })
        .WithName("Realms_Get")
        .RequiresPermission("control-plane:realm:read");

        group.MapPost("", async (CreateRealmDto dto, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateRealmAsync(dto, ct);
            return result.ToResult(realm => Results.Created($"{path}/admin/realms/{realm.Slug}", MapToDto(realm)));
        })
        .WithName("Realms_Create")
        .RequiresPermission("control-plane:realm:write");

        group.MapPatch("{slug}", async (string slug, UpdateRealmDto dto, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.UpdateRealmAsync(slug, dto, ct);
            return result.ToResult(realm => Results.Ok(MapToDto(realm)));
        })
        .WithName("Realms_Update")
        .RequiresPermission("control-plane:realm:write");

        group.MapDelete("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteRealmAsync(slug, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Realms_Delete")
        .RequiresPermission("control-plane:realm:write");

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
// the Authentication slice's SetupEndpoints can apply the same filter
// without an Api ↔ Auth circular reference (C14b).
