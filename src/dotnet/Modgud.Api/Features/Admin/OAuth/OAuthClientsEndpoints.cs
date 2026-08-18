using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Features.Management;
using Modgud.Api.Features.Positions;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Services;

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

        // Client creation is deliberately exposed through the Management API:
        // it is the generic provisioning seam for both ordinary clients and
        // terminal-managed clients. The remaining group stays cookie-only.
        app.MapPost($"{path}/admin/oauth/clients", async (CreateOAuthClientDto dto, HttpContext http, AppSettings settings, IPermissionService permissions, OAuthAdminService svc, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
        {
            var actorId = ResolveActorId(http);
            if (actorId is null) return Results.Unauthorized();

            if (dto.NewServiceAccount is not null ||
                !string.IsNullOrWhiteSpace(dto.LinkedServiceAccountId))
            {
                if (!await permissions.HasPermissionAsync(
                        actorId.Value, AppSlugs.Modgud, "service-account:write", ct))
                    return ManagementForbidden("service-account:write");
            }

            // MG-FT — terminal-managed create: 404 while the feature flag is off
            // (mirrors the position endpoints), and creating/linking a position's
            // slot needs position:write on top of oauth-client:write.
            if (OAuthAdminService.HasTerminalClientIntent(dto))
            {
                if (!settings.Features.PositionTerminals) return Results.NotFound();
                if (!await permissions.HasPermissionAsync(
                        actorId.Value, AppSlugs.Modgud, "position:write", ct))
                    return ManagementForbidden("position:write");
            }

            var result = await svc.CreateClientAsync(
                dto, dcrMetadata: null, enlistInTransaction: null, actorId: actorId.Value, ct);
            // Broadcast only the client view (never the one-time secret in the wrapper).
            if (!result.IsError && !result.Value.WasAlreadyProvisioned)
            {
                dispatcher.DispatchCreatedEvent("OAuthClient", result.Value.Client, session.TenantId);
                if (result.Value.CreatedServiceAccount is { } serviceAccount)
                    dispatcher.DispatchCreatedEvent("ServiceAccount", serviceAccount, session.TenantId);
                if (result.Value.CreatedPosition is { } position)
                    dispatcher.DispatchCreatedEvent("Position", position, session.TenantId);
                if (result.Value.CreatedTerminalId is { } terminalId && ShortGuid.TryParse(terminalId, out Guid terminalGuid))
                    dispatcher.DispatchCreatedEvent("Terminal",
                        await PositionTerminalsEndpoints.LoadDtoAsync(session, terminalGuid, ct), session.TenantId);
            }
            return result.ToResult(created => created.WasAlreadyProvisioned
                ? Results.Ok(created)
                : Results.Created($"{path}/admin/oauth/clients/{created.Client.Id}", created));
        })
        .WithName("OAuth_Clients_Create")
        .WithTags("OAuth Clients")
        .RequiresManagementPermission("oauth-client:write");

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

    private static Guid? ResolveActorId(HttpContext http)
    {
        var nameIdentifier = http.GetUserId();
        if (nameIdentifier.HasValue) return nameIdentifier;

        var subject = http.User.FindFirst("sub")?.Value;
        return Guid.TryParse(subject, out var principalId) ? principalId : null;
    }

    private static IResult ManagementForbidden(string permission) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: $"Missing the required permission '{permission}' (app '{AppSlugs.Modgud}').",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "Management.PermissionDenied",
            });
}
