using System.Text.Json;
using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authentication.Api.Admin.IdentityProviders.Commands;
using Cocoar.Auth.Application.DTOs.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Wolverine;

namespace Cocoar.Auth.Authentication.Api.Admin.IdentityProviders;

public static class IdpConfigEndpoints
{
    public static void MapIdpConfigEndpoints(this IEndpointRouteBuilder endpoints, string path)
    {
        var group = endpoints.MapGroup($"{path}/admin/idp-config")
            .RequireAuthorization();

        // List registered flavors so the admin UI can render the "Add provider"
        // picker with the right schema + defaults for each.
        group.MapGet("flavors",
            ([FromServices] FlavorRegistry flavors) =>
            {
                var items = flavors.All.Select(f => new FlavorDto
                {
                    Key = f.Key,
                    DisplayName = f.DisplayName,
                    DefaultIconName = f.DefaultIconName,
                    DefaultScopes = f.DefaultScopes.ToList(),
                    DefaultUserUpdateScript = f.DefaultUserUpdateScript,
                    DefaultStoreRawClaims = f.DefaultStoreRawClaims,
                    ConfigSchema = f.ConfigSchema.Select(c => new FlavorConfigFieldDto(
                        c.Key, c.Type.ToString(), c.Label, c.Required, c.HelpText, c.Placeholder)).ToList(),
                }).ToArray();
                return Results.Ok(items);
            })
            .RequiresPermission("cocoar-auth:idp-config:read");

        // List all non-deleted configs for the admin grid.
        group.MapGet("",
            async ([FromServices] IQuerySession session,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var items = await session.Query<IdpConfig>()
                    .Where(c => !c.IsDeleted)
                    .OrderBy(c => c.DisplayName)
                    .ToListAsync(ct);
                var publicUrl = ResolvePublicUrl(conf);
                return Results.Ok(items.Select(c => ToDto(c, publicUrl)).ToArray());
            })
            .RequiresPermission("cocoar-auth:idp-config:read");

        // Single config.
        group.MapGet("{id}",
            async (ShortGuid id,
                   [FromServices] IQuerySession session,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var c = await session.LoadAsync<IdpConfig>(id.Guid, ct);
                return c is null || c.IsDeleted
                    ? Results.NotFound()
                    : Results.Ok(ToDto(c, ResolvePublicUrl(conf)));
            })
            .RequiresPermission("cocoar-auth:idp-config:read");

        // Create via Wolverine command (see CreateIdpConfigCommand).
        group.MapPost("",
            async ([FromBody] CreateIdpConfigRequest request,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                JsonDocument? flavorData = request.FlavorData.HasValue
                    ? JsonDocument.Parse(request.FlavorData.Value.GetRawText())
                    : null;
                var command = new CreateIdpConfigCommand(request.Flavor, request.DisplayName, flavorData);
                var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(command, ct);
                return result.Match<IResult>(
                    v => Results.Created($"/api/admin/idp-config/{v.Id:N}", ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("cocoar-auth:idp-config:write");

        // Save full edit form (everything except secret).
        group.MapPut("{id}",
            async (ShortGuid id,
                   [FromBody] UpdateIdpConfigRequest request,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                JsonDocument? flavorData = request.FlavorData.HasValue
                    ? JsonDocument.Parse(request.FlavorData.Value.GetRawText())
                    : null;
                var command = new UpdateIdpConfigCommand(
                    Id: id.Guid,
                    DisplayName: request.DisplayName,
                    ClientId: request.ClientId,
                    Scopes: request.Scopes,
                    UserUpdateScript: request.UserUpdateScript,
                    StoreRawClaims: request.StoreRawClaims,
                    RawClaimsRetentionDays: request.RawClaimsRetentionDays,
                    AutoCreateUsers: request.AutoCreateUsers,
                    AllowLinking: request.AllowLinking,
                    TrustForEmailLink: request.TrustForEmailLink,
                    AllowedEmailDomains: request.AllowedEmailDomains,
                    IconName: request.IconName,
                    ButtonColorHex: request.ButtonColorHex,
                    FlavorData: flavorData);
                var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(command, ct);
                return result.Match<IResult>(
                    v => Results.Ok(ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("cocoar-auth:idp-config:write");

        // Enable / Disable / Delete.
        group.MapPost("{id}/enable",
            async (ShortGuid id,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(new EnableIdpConfigCommand(id.Guid), ct);
                return result.Match<IResult>(
                    v => Results.Ok(ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("cocoar-auth:idp-config:write");

        group.MapPost("{id}/disable",
            async (ShortGuid id,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(new DisableIdpConfigCommand(id.Guid), ct);
                return result.Match<IResult>(
                    v => Results.Ok(ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("cocoar-auth:idp-config:write");

        group.MapDelete("{id}",
            async (ShortGuid id,
                   [FromServices] IMessageBus bus,
                   CancellationToken ct) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<Success>>(new DeleteIdpConfigCommand(id.Guid), ct);
                return result.Match<IResult>(_ => Results.NoContent(), ErrorResponse);
            })
            .RequiresPermission("cocoar-auth:idp-config:write");

        // Secret rotation — the only endpoint that accepts a plaintext secret.
        group.MapPost("{id}/secret",
            async (ShortGuid id,
                   [FromBody] RotateSecretRequest request,
                   HttpContext http,
                   [FromServices] IMessageBus bus,
                   CancellationToken ct) =>
            {
                var userId = http.GetUserId();
                var result = await bus.InvokeAsync<ErrorOr<Success>>(
                    new RotateIdpConfigSecretCommand(id.Guid, request.Secret, userId), ct);
                return result.Match<IResult>(_ => Results.NoContent(), ErrorResponse);
            })
            .RequiresPermission("cocoar-auth:idp-config:write");
    }

    private static string ResolvePublicUrl(IServerConfiguration conf)
        => conf.PublicUrl ?? conf.AppUrl ?? "http://localhost:8081";

    private static IdpConfigDto ToDto(IdpConfig c, string publicUrl) => new()
    {
        Id = new ShortGuid(c.Id).ToString(),
        Flavor = c.Flavor,
        DisplayName = c.DisplayName,
        Enabled = c.Enabled,
        ClientId = c.ClientId,
        HasClientSecret = c.ClientSecretEncrypted is { Length: > 0 },
        Scopes = c.Scopes,
        UserUpdateScript = c.UserUpdateScript,
        StoreRawClaims = c.StoreRawClaims,
        RawClaimsRetentionDays = c.RawClaimsRetentionDays,
        AutoCreateUsers = c.AutoCreateUsers,
        AllowLinking = c.AllowLinking,
        TrustForEmailLink = c.TrustForEmailLink,
        AllowedEmailDomains = c.AllowedEmailDomains,
        IconName = c.IconName,
        ButtonColorHex = c.ButtonColorHex,
        FlavorData = c.FlavorData is null ? null : JsonDocument.Parse(c.FlavorData.RootElement.GetRawText()).RootElement,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
        RedirectUri = $"{publicUrl.TrimEnd('/')}/signin-oidc/{c.Id:N}",
    };

    private static IResult ErrorResponse(List<Error> errors)
    {
        var first = errors[0];
        return first.Type switch
        {
            ErrorType.NotFound => Results.NotFound(new { first.Code, Message = first.Description }),
            ErrorType.Conflict => Results.Conflict(new { first.Code, Message = first.Description }),
            _ => Results.BadRequest(new { first.Code, Message = first.Description }),
        };
    }

    public record CreateIdpConfigRequest(string Flavor, string DisplayName, JsonElement? FlavorData);

    public record UpdateIdpConfigRequest(
        string DisplayName,
        string ClientId,
        List<string> Scopes,
        string UserUpdateScript,
        bool StoreRawClaims,
        int? RawClaimsRetentionDays,
        bool AutoCreateUsers,
        bool AllowLinking,
        bool TrustForEmailLink,
        List<string>? AllowedEmailDomains,
        string? IconName,
        string? ButtonColorHex,
        JsonElement? FlavorData);

    public record RotateSecretRequest(string Secret);
}
