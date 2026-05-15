using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Domain.OAuth.Apis;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Api.Tests.Authorization;

/// <summary>
/// Pins the catalog-edit safety net: removing an <see cref="AppPermission"/>
/// that's still referenced by a <see cref="PermissionRole"/> or
/// <see cref="OAuthApiState"/> must be refused with HTTP 409 + a list of
/// the blocking references. Without this, an admin would silently revoke
/// every grant pointing at the dropped catalog id.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AppCatalogDeleteBlockTests : IntegrationTestBase
{
    public AppCatalogDeleteBlockTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PUT_removing_unreferenced_entry_succeeds()
    {
        // No role/RS references the catalog → drop is allowed.
        var (appId, _) = await SeedAppWithCatalogAsync("alpha", [("policy", "read"), ("policy", "write")]);

        var response = await Client.PutAsJsonAsync(
            $"/api/app/{new ShortGuid(appId)}",
            new
            {
                DisplayName = "Alpha",
                Description = (string?)null,
                Permissions = new[]
                {
                    // Only policy:read survives — policy:write is removed.
                    new { Resource = "policy", Action = "read", Description = (string?)null },
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PUT_removing_role_referenced_entry_returns_409_with_role_in_blockers()
    {
        // role.PermissionIds contains policy:write's id → removing policy:write
        // from the catalog must be refused.
        var (appId, perms) = await SeedAppWithCatalogAsync("beta", [("policy", "read"), ("policy", "write")]);
        var policyWriteId = perms.First(p => p.Resource == "policy" && p.Action == "write").Id;
        await SeedRoleAsync("Beta Editor", appId, [policyWriteId]);

        var response = await Client.PutAsJsonAsync(
            $"/api/app/{new ShortGuid(appId)}",
            new
            {
                DisplayName = "Beta",
                Description = (string?)null,
                Permissions = new[]
                {
                    // policy:write removed.
                    new { Resource = "policy", Action = "read", Description = (string?)null },
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal("App.CatalogEntriesReferenced", root.GetProperty("Error").GetString());
        var blockers = root.GetProperty("Blockers");
        Assert.Equal(1, blockers.GetArrayLength());
        var blocker = blockers[0];
        Assert.Equal("policy:write", blocker.GetProperty("Permission").GetString());
        var roles = blocker.GetProperty("ReferencedByRoles").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("Beta Editor", roles);
    }

    [Fact]
    public async Task PUT_removing_oauthapi_referenced_entry_returns_409_with_api_in_blockers()
    {
        // OAuthApi.PermissionIds contains policy:write's id → removing
        // policy:write must be refused, with the API listed.
        var (appId, perms) = await SeedAppWithCatalogAsync("gamma", [("policy", "read"), ("policy", "write")]);
        var policyWriteId = perms.First(p => p.Resource == "policy" && p.Action == "write").Id;
        await SeedOAuthApiAsync("https://gamma-api.example.com", appId, [policyWriteId]);

        var response = await Client.PutAsJsonAsync(
            $"/api/app/{new ShortGuid(appId)}",
            new
            {
                DisplayName = "Gamma",
                Description = (string?)null,
                Permissions = new[]
                {
                    new { Resource = "policy", Action = "read", Description = (string?)null },
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var blocker = json.RootElement.GetProperty("Blockers")[0];
        Assert.Equal("policy:write", blocker.GetProperty("Permission").GetString());
        var rsNames = blocker.GetProperty("ReferencedByResourceServers").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("https://gamma-api.example.com", rsNames);
    }

    [Fact]
    public async Task PUT_renaming_entry_succeeds_even_when_referenced()
    {
        // Renaming changes Resource/Action but the Id is preserved (the
        // payload echoes the existing id). FK references survive — no 409.
        var (appId, perms) = await SeedAppWithCatalogAsync("delta", [("policy", "write")]);
        var policyWriteId = perms.First().Id;
        await SeedRoleAsync("Delta Editor", appId, [policyWriteId]);

        var response = await Client.PutAsJsonAsync(
            $"/api/app/{new ShortGuid(appId)}",
            new
            {
                DisplayName = "Delta",
                Description = (string?)null,
                Permissions = new[]
                {
                    // Same id, but renamed to "policy:edit".
                    new
                    {
                        Id = new ShortGuid(policyWriteId).ToString(),
                        Resource = "policy",
                        Action = "edit",
                        Description = (string?)null,
                    },
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<(Guid AppId, List<AppPermission> Permissions)> SeedAppWithCatalogAsync(
        string slug, IReadOnlyList<(string Resource, string Action)> catalog)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var permissions = catalog
            .Select(c => new AppPermission(Guid.NewGuid(), c.Resource, c.Action, Description: null))
            .ToList();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null,
            Permissions: permissions, IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (id, permissions);
    }

    private async Task SeedRoleAsync(string name, Guid appId, List<Guid> permissionIds)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // PermissionRoleProjection (inline) writes the doc from the event —
        // direct Store conflicts under Marten 8.34+ optimistic concurrency.
        var role = new PermissionRole
        {
            Id = Guid.NewGuid(),
            Name = name,
            AppId = appId,
            IsRealmAdmin = false,
            PermissionIds = permissionIds,
        };
        session.Events.StartStream(role.Id, new PermissionRoleCreatedEvent(
            role.Id, role.Name, role.Description, role.AppId, role.IsRealmAdmin, role.PermissionIds));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedOAuthApiAsync(string name, Guid appId, List<Guid> permissionIds)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        var (aggregate, created) = Cocoar.Auth.Domain.OAuth.Apis.OAuthApiAggregate.Create(
            id, name, displayName: name, description: null, enabled: true,
            scopes: Array.Empty<string>());
        session.Events.StartStream<Cocoar.Auth.Domain.OAuth.Apis.OAuthApiAggregate>(id, created);
        session.Events.Append(id, aggregate.SetAppId(appId));
        if (permissionIds.Count > 0)
            session.Events.Append(id, aggregate.SetPermissionIds(permissionIds));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
