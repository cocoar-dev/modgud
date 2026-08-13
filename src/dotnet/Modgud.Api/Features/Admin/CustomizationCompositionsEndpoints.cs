using System.Text.Json;
using Marten;
using Modgud.Authorization.AspNetCore;
using Modgud.Domain.RealmSettings;
using Modgud.Domain.Realms;

namespace Modgud.Api.Features.Admin;

public record CreatePageCompositionRequest(string? Name, JsonElement Root);
public record PublishPageCompositionRequest(string? BaseVersion, JsonElement Root);

/// <summary>
/// Host-owned repository adapter for reusable PageBuilder compositions.
/// Realm tenancy, permissions, transport and immutable version persistence stay
/// outside the generic UI package.
/// </summary>
public static class CustomizationCompositionsEndpoints
{
    private const int MaxNameLength = 80;
    private const int MaxCompositions = 100;
    private const int MaxVersions = 100;

    public static WebApplication MapCustomizationCompositionsEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/customization/compositions")
            .WithTags("Admin Customization Compositions")
            .RequireAuthorization();

        group.MapGet("", async (AppSettings settings, IDocumentSession session, CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            var realm = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            var result = (realm?.PageCompositions ?? [])
                .OrderBy(item => item.Name)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    LatestVersion = item.Versions.Max(version => version.Number).ToString(),
                    Versions = item.Versions.OrderByDescending(version => version.Number)
                        .Select(version => version.Number.ToString()).ToArray(),
                }).ToArray();
            return Results.Ok(result);
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_ListCompositions");

        group.MapGet("{id}", async (
            string id,
            string? version,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            var realm = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            var composition = realm?.PageCompositions?.FirstOrDefault(item => item.Id == id);
            if (composition is null) return Results.NotFound();
            var definition = int.TryParse(version, out var number)
                ? composition.Versions.FirstOrDefault(item => item.Number == number)
                : composition.Versions.MaxBy(item => item.Number);
            return definition is null
                ? Results.NotFound()
                : Results.Ok(Definition(composition, definition));
        })
        .RequiresPermission("realm-settings:read")
        .WithName("Admin_Customization_GetComposition");

        group.MapPost("", async (
            CreatePageCompositionRequest body,
            HttpContext http,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            if (!ValidName(body.Name, out var nameError)) return Results.BadRequest(new { Message = nameError });
            var root = body.Root.GetRawText();
            if (!PageCompositionDocumentService.ValidateCompositionRoot(root, out var rootError))
                return Results.BadRequest(new { Message = rootError });

            var realm = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct)
                ?? new RealmSettings { Id = RealmSettings.SingletonId, CreatedAt = DateTimeOffset.UtcNow };
            realm.PageCompositions ??= [];
            if (realm.PageCompositions.Count >= MaxCompositions)
                return Results.BadRequest(new { Message = $"Too many compositions (max {MaxCompositions})." });

            var now = DateTimeOffset.UtcNow;
            var composition = new PageComposition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = body.Name!.Trim(),
                CreatedAt = now,
                Versions =
                [
                    new PageCompositionVersion
                    {
                        Number = 1,
                        Root = root,
                        PublishedAt = now,
                        PublishedBy = http.User.Identity?.Name,
                    },
                ],
            };
            realm.PageCompositions.Add(composition);
            realm.UpdatedAt = now;
            session.Store(realm);
            await session.SaveChangesAsync(ct);
            return Results.Ok(Definition(composition, composition.Versions[0]));
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_CreateComposition");

        group.MapPost("{id}/versions", async (
            string id,
            PublishPageCompositionRequest body,
            HttpContext http,
            AppSettings settings,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            if (!settings.Features.PageBuilder) return Results.NotFound();
            var root = body.Root.GetRawText();
            if (!PageCompositionDocumentService.ValidateCompositionRoot(root, out var rootError))
                return Results.BadRequest(new { Message = rootError });

            var realm = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
            var composition = realm?.PageCompositions?.FirstOrDefault(item => item.Id == id);
            if (composition is null) return Results.NotFound();
            var current = composition.Versions.MaxBy(item => item.Number)!;
            if (body.BaseVersion != current.Number.ToString())
                return Results.Conflict(new
                {
                    Message = $"Composition {id} changed from {body.BaseVersion ?? "unknown"} to {current.Number}; reload before publishing.",
                    CurrentVersion = current.Number.ToString(),
                });
            if (composition.Versions.Count >= MaxVersions)
                return Results.BadRequest(new { Message = $"Too many versions (max {MaxVersions})." });

            // A definition may nest other definitions, but never itself. Full
            // transitive cycle validation happens whenever a page is published.
            if (ContainsReferenceTo(root, id))
                return Results.BadRequest(new { Message = $"Composition {id} cannot contain itself." });

            var now = DateTimeOffset.UtcNow;
            var version = new PageCompositionVersion
            {
                Number = current.Number + 1,
                Root = root,
                PublishedAt = now,
                PublishedBy = http.User.Identity?.Name,
            };
            composition.Versions.Add(version);
            composition.UpdatedAt = now;
            realm!.UpdatedAt = now;
            session.Store(realm);
            await session.SaveChangesAsync(ct);
            return Results.Ok(Definition(composition, version));
        })
        .RequiresPermission("realm-settings:write")
        .WithName("Admin_Customization_PublishComposition");

        return app;
    }

    private static object Definition(PageComposition composition, PageCompositionVersion version) => new
    {
        composition.Id,
        composition.Name,
        Version = version.Number.ToString(),
        Root = JsonSerializer.Deserialize<JsonElement>(version.Root),
    };

    private static bool ValidName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Composition name is required.";
            return false;
        }
        if (name.Trim().Length > MaxNameLength)
        {
            error = $"Composition name exceeds {MaxNameLength} characters.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool ContainsReferenceTo(string rootJson, string id)
    {
        using var document = JsonDocument.Parse(rootJson);
        bool Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return false;
            if (node.TryGetProperty("composition", out var reference)
                && reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("id", out var referenceId)
                && referenceId.GetString() == id)
                return true;
            return node.TryGetProperty("children", out var children)
                && children.ValueKind == JsonValueKind.Array
                && children.EnumerateArray().Any(Walk);
        }
        return Walk(document.RootElement);
    }
}
