using System.Text.Json;
using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Modgud.Authorization.AspNetCore;
using Modgud.Authentication;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Application.DTOs.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders;
using Wolverine;

namespace Modgud.Authentication.Api.Admin.LoginProviders;

/// <summary>
/// Admin surface for the consolidated <c>LoginProvider</c> aggregate. Replaces
/// the legacy <c>idp-config</c> endpoints — same OIDC concerns now live behind
/// the <c>login-provider</c> resource, and Internal-typed providers are part
/// of the same surface.
/// </summary>
public static class LoginProvidersEndpoints
{
    public static void MapLoginProvidersEndpoints(this IEndpointRouteBuilder endpoints, string path)
    {
        var group = endpoints.MapGroup($"{path}/admin/login-providers")
            .WithTags("Login Providers")
            .RequireAuthorization();

        // List registered flavors so the admin UI can render the "Add provider"
        // picker with the right schema + defaults for each.
        group.MapGet("flavors",
            ([FromServices] LoginProviderFlavorRegistry flavors) =>
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
            .RequiresPermission("login-provider:read");

        // List all non-deleted providers for the admin grid.
        group.MapGet("",
            async ([FromServices] IQuerySession session,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var items = await session.Query<LoginProvider>()
                    .Where(c => !c.IsDeleted)
                    .OrderBy(c => c.DisplayName)
                    .ToListAsync(ct);
                var publicUrl = ResolvePublicUrl(conf);
                return Results.Ok(items.Select(c => ToDto(c, publicUrl)).ToArray());
            })
            .RequiresPermission("login-provider:read");

        // Single provider.
        group.MapGet("{id}",
            async (ShortGuid id,
                   [FromServices] IQuerySession session,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var c = await session.LoadAsync<LoginProvider>(id.Guid, ct);
                return c is null || c.IsDeleted
                    ? Results.NotFound()
                    : Results.Ok(ToDto(c, ResolvePublicUrl(conf)));
            })
            .RequiresPermission("login-provider:read");

        // Create via Wolverine command.
        group.MapPost("",
            async ([FromBody] CreateLoginProviderRequest request,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                JsonDocument? flavorData = request.FlavorData.HasValue
                    ? JsonDocument.Parse(request.FlavorData.Value.GetRawText())
                    : null;
                var command = new CreateLoginProviderCommand(
                    Flavor: request.Flavor,
                    DisplayName: request.DisplayName,
                    FlavorData: flavorData,
                    Type: request.Type ?? LoginProviderType.Oidc,
                    Description: request.Description);
                var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(command, ct);
                return result.Match<IResult>(
                    v => Results.Created($"/api/admin/login-providers/{v.Id:N}", ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("login-provider:write");

        // Save full edit form (everything except secret).
        group.MapPut("{id}",
            async (ShortGuid id,
                   [FromBody] UpdateLoginProviderRequest request,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                JsonDocument? flavorData = request.FlavorData.HasValue
                    ? JsonDocument.Parse(request.FlavorData.Value.GetRawText())
                    : null;
                var command = new UpdateLoginProviderCommand(
                    Id: id.Guid,
                    DisplayName: request.DisplayName,
                    Description: request.Description,
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
                var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(command, ct);
                return result.Match<IResult>(
                    v => Results.Ok(ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("login-provider:write");

        // Enable / Disable / Delete.
        group.MapPost("{id}/enable",
            async (ShortGuid id,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new EnableLoginProviderCommand(id.Guid), ct);
                return result.Match<IResult>(
                    v => Results.Ok(ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("login-provider:write");

        group.MapPost("{id}/disable",
            async (ShortGuid id,
                   [FromServices] IMessageBus bus,
                   [FromServices] IServerConfiguration conf,
                   CancellationToken ct) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new DisableLoginProviderCommand(id.Guid), ct);
                return result.Match<IResult>(
                    v => Results.Ok(ToDto(v, ResolvePublicUrl(conf))),
                    ErrorResponse);
            })
            .RequiresPermission("login-provider:write");

        group.MapDelete("{id}",
            async (ShortGuid id,
                   [FromServices] IMessageBus bus,
                   CancellationToken ct) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<Success>>(new DeleteLoginProviderCommand(id.Guid), ct);
                return result.Match<IResult>(_ => Results.NoContent(), ErrorResponse);
            })
            .RequiresPermission("login-provider:write");

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
                    new RotateLoginProviderSecretCommand(id.Guid, request.Secret, userId), ct);
                return result.Match<IResult>(_ => Results.NoContent(), ErrorResponse);
            })
            .RequiresPermission("login-provider:write");
    }

    private static string ResolvePublicUrl(IServerConfiguration conf)
        => conf.PublicUrl ?? conf.AppUrl ?? "http://localhost:8081";

    private static LoginProviderDto ToDto(LoginProvider c, string publicUrl) => new()
    {
        Id = new ShortGuid(c.Id).ToString(),
        Type = c.Type.ToString(),
        Flavor = c.Flavor,
        DisplayName = c.DisplayName,
        Description = c.Description,
        IsBuiltIn = c.IsBuiltIn,
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
        // Internal providers have no callback — return an empty string instead
        // of inventing a meaningless URL.
        RedirectUri = c.Type == LoginProviderType.Internal
            ? string.Empty
            : $"{publicUrl.TrimEnd('/')}/signin-oidc/{c.Id:N}",
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

    public record CreateLoginProviderRequest(
        string Flavor,
        string DisplayName,
        JsonElement? FlavorData,
        LoginProviderType? Type = LoginProviderType.Oidc,
        string? Description = null);

    public record UpdateLoginProviderRequest(
        string DisplayName,
        string? Description,
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
