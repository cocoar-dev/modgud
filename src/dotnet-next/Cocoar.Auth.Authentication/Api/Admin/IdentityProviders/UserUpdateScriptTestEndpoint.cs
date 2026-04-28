using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;

namespace Cocoar.Auth.Authentication.Api.Admin.IdentityProviders;

/// <summary>
/// Admin helpers for authoring the per-IdP user-update script: run a test
/// evaluation against sample claims, and load the last raw claims that the
/// IdP sent during a real login so the admin can iterate without triggering
/// end-user logins.
/// </summary>
public static class UserUpdateScriptTestEndpoint
{
    public static void MapUserUpdateScriptTestEndpoint(this IEndpointRouteBuilder endpoints, string path)
    {
        var group = endpoints.MapGroup($"{path}/admin/idp-config")
            .RequireAuthorization()
            // Read-only test/preview surface — gated on idp-config:read so an
            // operator can prototype a script without write access.
            .RequiresPermission("idp-config:read");

        // Test-run the script with arbitrary sample claims. Accepts either the
        // IdpConfig's stored script (by id) or a proposed script body (unsaved
        // editor content).
        group.MapPost("{id}/test-user-update",
            async (ShortGuid id,
                   [FromBody] TestUserUpdateRequest request,
                   [FromServices] IQuerySession session,
                   [FromServices] UserUpdateScriptRunner runner,
                   CancellationToken ct) =>
            {
                var script = request.Script;
                if (string.IsNullOrWhiteSpace(script))
                {
                    var config = await session.LoadAsync<IdpConfig>(id.Guid, ct);
                    if (config is null) return Results.NotFound();
                    script = config.UserUpdateScript;
                }

                var claims = request.Claims ?? new Dictionary<string, JsonElement>();
                var asDict = claims.ToDictionary(
                    kv => kv.Key,
                    kv => (object?)JsonElementToClr(kv.Value));

                var result = runner.Run(script ?? string.Empty, asDict);
                return Results.Ok(new TestUserUpdateResponse(
                    Succeeded: result.Succeeded,
                    Error: result.Error,
                    Firstname: FieldToDto(result.Firstname),
                    Lastname: FieldToDto(result.Lastname),
                    Email: FieldToDto(result.Email),
                    Acronym: FieldToDto(result.Acronym),
                    ScriptOutput: result.ScriptOutput is null
                        ? null
                        : JsonDocument.Parse(result.ScriptOutput.RootElement.GetRawText()).RootElement));
            });

        // Load the last raw claims from the most recent login via this IdP.
        // Used by the admin editor's "Load last login sample" button.
        group.MapGet("{id}/last-raw-claims",
            async (ShortGuid id,
                   [FromServices] IQuerySession session,
                   CancellationToken ct) =>
            {
                var configId = id.Guid;
                var link = await session.Query<ExternalIdentityLink>()
                    .Where(l => l.IdpConfigId == configId && !l.IsUnlinked && l.LastRawClaims != null)
                    .OrderByDescending(l => l.LastLoginAt)
                    .FirstOrDefaultAsync(ct);

                if (link?.LastRawClaims is null)
                    return Results.Ok(new { Available = false });

                return Results.Ok(new
                {
                    Available = true,
                    link.LastLoginAt,
                    RawClaims = link.LastRawClaims,
                });
            });
    }

    private static object? JsonElementToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToClr).ToArray(),
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToClr(p.Value)),
        _ => null,
    };

    private static FieldPatchDto FieldToDto(FieldPatch patch) => new(
        Presence: patch.Presence.ToString(),
        Value: patch.Value);

    public record TestUserUpdateRequest(
        string? Script,
        Dictionary<string, JsonElement>? Claims);

    public record TestUserUpdateResponse(
        bool Succeeded,
        string? Error,
        FieldPatchDto Firstname,
        FieldPatchDto Lastname,
        FieldPatchDto Email,
        FieldPatchDto Acronym,
        JsonElement? ScriptOutput);

    public record FieldPatchDto(string Presence, string? Value);
}
