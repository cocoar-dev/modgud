using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authorization.AspNetCore;

namespace Cocoar.Auth.Api.Features.Admin.OAuth;

public static class OAuthApisEndpoints
{
    public static WebApplication MapOAuthApisEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/oauth/apis")
            .WithTags("OAuth APIs")
            .RequireAuthorization();

        group.MapGet("", async (OAuthAdminService svc, int page, int pageSize, CancellationToken ct) =>
        {
            var pagination = PaginationRequest.WithDefaults(page, pageSize);
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

        return app;
    }
}
