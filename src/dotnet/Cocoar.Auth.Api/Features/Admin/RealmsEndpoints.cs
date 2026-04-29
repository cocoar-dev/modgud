using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;

namespace Cocoar.Auth.Api.Features.Admin;

/// <summary>
/// Realm management endpoints. Only callable from realms with
/// <see cref="Realm.CanManageTenants"/> enabled (typically the system realm).
/// Gated by <c>app:admin</c> on top of the realm-level capability check.
/// </summary>
public static class RealmsEndpoints
{
    public static WebApplication MapRealmsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/realms")
            .WithTags("Realms")
            .RequireAuthorization()
            .AddEndpointFilter<RequireCanManageTenantsFilter>();

        group.MapGet("", async (IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var realms = await svc.GetAllRealmsAsync(ct);
            var items = realms.Select(MapToDto).ToList();
            return Results.Ok(new RealmListDto { Items = items, TotalCount = items.Count });
        })
        .WithName("Realms_List")
        .RequiresPermission("realm:read");

        group.MapGet("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var realm = await svc.GetRealmBySlugAsync(slug, ct);
            return realm is null ? Results.NotFound() : Results.Ok(MapToDto(realm));
        })
        .WithName("Realms_Get")
        .RequiresPermission("realm:read");

        group.MapPost("", async (CreateRealmDto dto, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateRealmAsync(dto, ct);
            return result.ToResult(realm => Results.Created($"{path}/admin/realms/{realm.Slug}", MapToDto(realm)));
        })
        .WithName("Realms_Create")
        .RequiresPermission("realm:write");

        group.MapPatch("{slug}", async (string slug, UpdateRealmDto dto, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.UpdateRealmAsync(slug, dto, ct);
            return result.ToResult(realm => Results.Ok(MapToDto(realm)));
        })
        .WithName("Realms_Update")
        .RequiresPermission("realm:write");

        group.MapDelete("{slug}", async (string slug, IRealmProvisioningService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteRealmAsync(slug, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Realms_Delete")
        .RequiresPermission("realm:write");

        return application;
    }

    private static RealmDto MapToDto(Realm realm) => new()
    {
        Id = realm.Id,
        Slug = realm.Slug,
        DisplayName = realm.DisplayName,
        Description = realm.Description,
        Domains = realm.Domains,
        CanManageTenants = realm.CanManageTenants,
        IsActive = realm.IsActive,
        NeedsSetup = false, // per-realm setup detection comes in a later etappe
        CreatedAt = realm.CreatedAt,
    };
}

/// <summary>
/// Endpoint filter that returns 404 when the request's resolved realm does not
/// have <see cref="Realm.CanManageTenants"/> enabled. Mirrors the legacy
/// <c>CanManageTenantsAttribute</c>.
/// </summary>
public sealed class RequireCanManageTenantsFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var info = context.HttpContext.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
        if (info is null || !info.CanManageTenants)
            return ValueTask.FromResult<object?>(Results.NotFound());

        return next(context);
    }
}
