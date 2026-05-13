using Cocoar.Auth.Application.DTOs.RealmSettings;
using Cocoar.Auth.Authentication.RealmSettings;
using Cocoar.Auth.Authorization.AspNetCore;

namespace Cocoar.Auth.Authentication.Api.Admin;

/// <summary>
/// Realm-admin surface for tenant-scoped realm-wide settings. Lives
/// outside the Control-Plane gate — every realm-admin (incl. CP-admin
/// in their own system realm) hits these endpoints against THEIR realm
/// only, because the underlying <c>IDocumentSession</c> is tenant-scoped
/// via the standard middleware.
///
/// <para>Permissions: <c>realm-settings:read</c> / <c>:write</c>. The
/// <c>realm:admin</c> bypass grants both. CP-admin reaching this from
/// their realm sees the system-realm settings; cross-realm admin still
/// goes through <c>/api/admin/realms/*</c> for structural metadata.</para>
/// </summary>
public static class RealmSettingsEndpoints
{
    public static WebApplication MapRealmSettingsEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/realm-settings")
            .WithTags("RealmSettings")
            .RequireAuthorization();

        group.MapGet("", async (
            IRealmSettingsService svc,
            CancellationToken ct) =>
        {
            var dto = await svc.GetDtoAsync(ct);
            return Results.Ok(dto);
        })
        .WithName("RealmSettings_Get")
        .RequiresPermission("realm-settings:read");

        group.MapPatch("", async (
            UpdateRealmSettingsDto dto,
            IRealmSettingsService svc,
            CancellationToken ct) =>
        {
            var result = await svc.PatchAsync(dto, ct);
            return result.Match(
                ok => Results.Ok(ok),
                errors => Results.Problem(
                    statusCode: errors.First().Type == ErrorOr.ErrorType.Validation
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status500InternalServerError,
                    title: errors.First().Code,
                    detail: errors.First().Description));
        })
        .WithName("RealmSettings_Patch")
        .RequiresPermission("realm-settings:write");

        return app;
    }
}
