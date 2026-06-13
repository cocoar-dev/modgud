using BuildingBlocks.EventDispatcher;
using Marten;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Api.Features.Admin.OAuth;

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

        // page + pageSize are nullable so a vanilla `GET /api/admin/oauth/clients`
        // (no query string) doesn't 400 on missing-required-params binding.
        // `WithDefaults` clamps null → 0 → defaults (page 1, pageSize 20).
        group.MapGet("", async (OAuthAdminService svc, int? page, int? pageSize, CancellationToken ct) =>
        {
            var pagination = PaginationRequest.WithDefaults(page ?? 0, pageSize ?? 0);
            return Results.Ok(await svc.GetClientsAsync(pagination, ct));
        })
        .WithName("OAuth_Clients_List")
        .RequiresPermission("oauth-client:read");

        group.MapGet("{id}", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var dto = await svc.GetClientByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("OAuth_Clients_Get")
        .RequiresPermission("oauth-client:read");

        group.MapPost("", async (CreateOAuthClientDto dto, OAuthAdminService svc, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await svc.CreateClientAsync(dto, ct);
            // Broadcast only the client view (never the one-time secret in the wrapper).
            if (!result.IsError)
                dispatcher.DispatchCreatedEvent("OAuthClient", result.Value.Client, session.TenantId);
            return result.ToResult(created => Results.Created($"{path}/admin/oauth/clients/{created.Client.Id}", created));
        })
        .WithName("OAuth_Clients_Create")
        .RequiresPermission("oauth-client:write");

        group.MapPut("{id}", async (string id, UpdateOAuthClientDto dto, OAuthAdminService svc, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await svc.UpdateClientAsync(id, dto, ct);
            if (!result.IsError)
                dispatcher.DispatchUpdatedEvent("OAuthClient", result.Value, session.TenantId);
            return result.ToResult(client => Results.Ok(client));
        })
        .WithName("OAuth_Clients_Update")
        .RequiresPermission("oauth-client:write");

        group.MapDelete("{id}", async (string id, OAuthAdminService svc, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await svc.DeleteClientAsync(id, ct);
            if (result.IsError) return result.ToResult();
            // The DTO.Id the SPA stores is the full Guid string (MapClient), so
            // emit the same here — a ShortGuid wouldn't match the grid row.
            dispatcher.DispatchDeletedEvent("OAuthClient", id, session.TenantId);
            return Results.NoContent();
        })
        .WithName("OAuth_Clients_Delete")
        .RequiresPermission("oauth-client:write");

        group.MapPost("{id}/regenerate-secret", async (string id, OAuthAdminService svc, CancellationToken ct) =>
        {
            var result = await svc.RegenerateClientSecretAsync(id, ct);
            return result.ToResult(secret => Results.Ok(secret));
        })
        .WithName("OAuth_Clients_RegenerateSecret")
        .RequiresPermission("oauth-client:write");

        return app;
    }
}
