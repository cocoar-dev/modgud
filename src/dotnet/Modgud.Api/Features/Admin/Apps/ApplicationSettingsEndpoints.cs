using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Applications;
using Modgud.Authentication.Applications;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Api.Features.Admin.Apps;

/// <summary>
/// ADR-0011 — admin surface for a per-Application settings override (the
/// tenant-scoped <c>ApplicationSettings</c> doc keyed by <c>App.Id</c>). Sparse:
/// GET returns only what the App overrides (null section = inherits the realm);
/// PATCH replaces provided sections (a null section = no change). Setting
/// <c>Origin.Subdomain</c> also writes the global host→App routing map. Gated by
/// the same <c>app:read</c>/<c>app:write</c> permissions as the rest of App admin.
/// </summary>
public static class ApplicationSettingsEndpoints
{
    public static WebApplication MapApplicationSettingsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/app")
            .WithTags("Application Settings")
            .RequireAuthorization();

        group.MapGet("{id}/settings", async (
                ShortGuid id,
                IApplicationSettingsService svc,
                CancellationToken ct) =>
            {
                var result = await svc.GetAsync(id.Guid, ct);
                return result.Match(Results.Ok, Problem);
            })
            .WithName("V2_App_Settings_Get")
            .RequiresPermission("app:read");

        group.MapPatch("{id}/settings", async (
                ShortGuid id,
                ApplicationSettingsDto dto,
                IApplicationSettingsService svc,
                CancellationToken ct) =>
            {
                var result = await svc.PatchAsync(id.Guid, dto, ct);
                return result.Match(Results.Ok, Problem);
            })
            .WithName("V2_App_Settings_Patch")
            .RequiresPermission("app:write");

        return application;
    }

    private static IResult Problem(List<ErrorOr.Error> errors)
    {
        var first = errors[0];
        var status = first.Type switch
        {
            ErrorOr.ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorOr.ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorOr.ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Problem(statusCode: status, title: first.Code, detail: first.Description);
    }
}
