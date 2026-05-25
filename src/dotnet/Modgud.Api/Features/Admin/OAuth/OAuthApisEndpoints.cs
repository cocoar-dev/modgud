using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Api.Features.Admin.OAuth;

public static class OAuthApisEndpoints
{
    public static WebApplication MapOAuthApisEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/oauth/apis")
            .WithTags("OAuth APIs")
            .RequireAuthorization();

        // page + pageSize are nullable so a vanilla `GET /api/admin/oauth/apis`
        // (no query string) doesn't 400 on missing-required-params binding.
        // `WithDefaults` clamps null → 0 → defaults (page 1, pageSize 20).
        group.MapGet("", async (OAuthAdminService svc, int? page, int? pageSize, CancellationToken ct) =>
        {
            var pagination = PaginationRequest.WithDefaults(page ?? 0, pageSize ?? 0);
            return Results.Ok(await svc.GetApisAsync(pagination, ct));
        })
        .WithName("OAuth_Apis_List")
        .RequiresPermission("oauth-api:read");

        group.MapGet("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var dto = await svc.GetApiByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("OAuth_Apis_Get")
        .RequiresPermission("oauth-api:read");

        group.MapPost("", async (CreateOAuthApiDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateApiAsync(dto, ct);
            return result.ToResult(created => Results.Created($"{path}/admin/oauth/apis/{created.Id}", created));
        })
        .WithName("OAuth_Apis_Create")
        .RequiresPermission("oauth-api:write");

        group.MapPut("{id}", async (string id, UpdateOAuthApiDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.UpdateApiAsync(id, dto, ct);
            return result.ToResult(api => Results.Ok(api));
        })
        .WithName("OAuth_Apis_Update")
        .RequiresPermission("oauth-api:write");

        group.MapDelete("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteApiAsync(id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("OAuth_Apis_Delete")
        .RequiresPermission("oauth-api:write");

        group.MapPost("{id}/regenerate-secret", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.RegenerateApiSecretAsync(id, ct);
            return result.ToResult(secret => Results.Ok(secret));
        })
        .WithName("OAuth_Apis_RegenerateSecret")
        .RequiresPermission("oauth-api:write");

        group.MapPost("{id}/secrets", async (string id, CreateApiSecretDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateApiSecretAsync(id, dto, ct);
            return result.ToResult(created => Results.Created($"{path}/admin/oauth/apis/{id}/secrets/{created.SecretId}", created));
        })
        .WithName("OAuth_Apis_CreateSecret")
        .RequiresPermission("oauth-api:write");

        group.MapDelete("{id}/secrets/{secretId}", async (string id, string secretId, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteApiSecretAsync(id, secretId, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("OAuth_Apis_DeleteSecret")
        .RequiresPermission("oauth-api:write");

        // One-click convenience: create the 1:1 OAuthScope companion for an
        // existing API. Eliminates the manual two-modal "API + matching
        // scope" flow that every single-RS integration ends up doing. The
        // permission gate is `oauth-scope:write` (not `oauth-api:write`)
        // because the side-effect that needs authorising is the scope
        // creation; an oauth-api admin who can't manage scopes shouldn't be
        // able to back-door one in.
        group.MapPost("{id}/create-implicit-scope", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateImplicitScopeForApiAsync(id, ct);
            return result.ToResult(scope => Results.Created($"/api/admin/oauth/scopes/{scope.Id}", scope));
        })
        .WithName("OAuth_Apis_CreateImplicitScope")
        .RequiresPermission("oauth-scope:write");

        return app;
    }
}
