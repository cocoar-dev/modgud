using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authorization.AspNetCore;

namespace Cocoar.Auth.Api.Features.Admin.OAuth;

/// <summary>
/// Admin endpoints for OAuth clients (applications). Per-realm — every operation
/// runs against the realm DB resolved by <c>TenantedSessionFactory</c>.
/// </summary>
public static class OAuthClientsEndpoints
{
    public static WebApplication MapOAuthClientsEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/oauth/clients")
            .WithTags("OAuth Clients")
            .RequireAuthorization();

        group.MapGet("", async (OAuthAdminService svc, int page, int pageSize, CancellationToken ct) =>
        {
            var pagination = PaginationRequest.WithDefaults(page, pageSize);
            return Results.Ok(await svc.GetClientsAsync(pagination, ct));
        })
        .WithName("OAuth_Clients_List")
        .RequiresPermission("cocoar-auth:oauth-client:read");

        group.MapGet("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var dto = await svc.GetClientByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("OAuth_Clients_Get")
        .RequiresPermission("cocoar-auth:oauth-client:read");

        group.MapPost("", async (CreateOAuthClientDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateClientAsync(dto, ct);
            return result.ToResult(created => Results.Created($"{path}/admin/oauth/clients/{created.Client.Id}", created));
        })
        .WithName("OAuth_Clients_Create")
        .RequiresPermission("cocoar-auth:oauth-client:write");

        group.MapPut("{id}", async (string id, UpdateOAuthClientDto dto, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.UpdateClientAsync(id, dto, ct);
            return result.ToResult(client => Results.Ok(client));
        })
        .WithName("OAuth_Clients_Update")
        .RequiresPermission("cocoar-auth:oauth-client:write");

        group.MapDelete("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteClientAsync(id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("OAuth_Clients_Delete")
        .RequiresPermission("cocoar-auth:oauth-client:write");

        group.MapPost("{id}/regenerate-secret", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.RegenerateClientSecretAsync(id, ct);
            return result.ToResult(secret => Results.Ok(secret));
        })
        .WithName("OAuth_Clients_RegenerateSecret")
        .RequiresPermission("cocoar-auth:oauth-client:write");

        return app;
    }
}
