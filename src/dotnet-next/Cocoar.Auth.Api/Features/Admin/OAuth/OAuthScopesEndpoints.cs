using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authorization.AspNetCore;

namespace Cocoar.Auth.Api.Features.Admin.OAuth;

public static class OAuthScopesEndpoints
{
    public static WebApplication MapOAuthScopesEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/oauth/scopes")
            .WithTags("OAuth Scopes")
            .RequireAuthorization();

        group.MapGet("", async (OAuthAdminService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetScopesAsync(ct)))
            .WithName("OAuth_Scopes_List")
            .RequiresPermission("oauth-scope:read");

        group.MapGet("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var dto = await svc.GetScopeByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("OAuth_Scopes_Get")
        .RequiresPermission("oauth-scope:read");

        group.MapPost("", async (CreateOAuthScopeDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateScopeAsync(dto, ct);
            return result.ToResult(scope => Results.Created($"{path}/admin/oauth/scopes/{scope.Id}", scope));
        })
        .WithName("OAuth_Scopes_Create")
        .RequiresPermission("oauth-scope:write");

        group.MapPut("{id}", async (string id, UpdateOAuthScopeDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.UpdateScopeAsync(id, dto, ct);
            return result.ToResult(scope => Results.Ok(scope));
        })
        .WithName("OAuth_Scopes_Update")
        .RequiresPermission("oauth-scope:write");

        group.MapDelete("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteScopeAsync(id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("OAuth_Scopes_Delete")
        .RequiresPermission("oauth-scope:write");

        return app;
    }
}
