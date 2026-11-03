using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Roles;
using Modgud.Infrastructure.Authorization;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

[Collection(IntegrationTestCollection.Name)]
public class InviteCodeCatalogTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_realm_catalog_exposes_invite_read_and_write(bool isControlPlane)
    {
        // System is provisioned by the real bootstrap fixture; the ordinary
        // tenant goes through the real provisioning service into a fresh DB.
        var tenantId = await GetRealmAsync(isControlPlane);
        var app = await GetSystemAppAsync(tenantId);
        Assert.True(app.IsSystem);
        AssertInviteCatalog(app);

        await using var session = GetTenantedSession(tenantId);
        var control = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == AppSlugs.ControlPlane, TestContext.Current.CancellationToken);
        if (isControlPlane)
        {
            Assert.NotNull(control);
            Assert.DoesNotContain(control.Permissions, p => p.Resource == "invite-code");
        }
        else
        {
            Assert.Null(control);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "read")]
    [InlineData(true, "write")]
    public async Task Existing_catalog_evolves_additively_without_granting_roles_and_is_idempotent(
        bool isControlPlane, string? retainedAction)
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await GetRealmAsync(isControlPlane);
        var original = await GetSystemAppAsync(tenantId);
        var oldPermissions = original.Permissions
            .Where(p => p.Resource != "invite-code" || p.Action == retainedAction).ToList();
        oldPermissions.Add(new AppPermission(Guid.NewGuid(), "custom-catalog", "read", "Keep me"));
        var existingGrant = oldPermissions.Single(p => p.Resource == "user" && p.Action == "read");
        var roleId = Guid.NewGuid();

        // Model a provisioned pre-fix catalog (or a manually completed half),
        // including a custom entry, custom metadata and an ordinary role FK.
        await using (var session = GetTenantedDocumentSession(tenantId))
        {
            session.Events.Append(original.Id, new AppUpdatedEvent(
                original.Id, "Local display name", "Local description", oldPermissions));
            session.Events.StartStream(roleId, new PermissionRoleCreatedEvent(
                roleId, "Existing reader", "Unchanged", original.Id, false, [existingGrant.Id]));
            await session.SaveChangesAsync(ct);
        }
        var beforeEvents = await EventCountAsync(tenantId, original.Id);
        var beforeRoles = await RoleSnapshotAsync(tenantId);
        await AppRealmSeeder.SeedAsync(Factory.Services, tenantId, isControlPlane, ct: ct);

        var evolved = await GetSystemAppAsync(tenantId);
        Assert.Equal(original.Id, evolved.Id);
        Assert.Equal(original.Slug, evolved.Slug);
        Assert.True(evolved.IsSystem);
        Assert.Equal("Local display name", evolved.DisplayName);
        Assert.Equal("Local description", evolved.Description);
        Assert.Equal(oldPermissions, evolved.Permissions.Take(oldPermissions.Count).ToList());
        Assert.Equal(oldPermissions.Count + (retainedAction is null ? 2 : 1), evolved.Permissions.Count);
        AssertInviteCatalog(evolved);
        Assert.Equal(beforeRoles, await RoleSnapshotAsync(tenantId));
        Assert.Equal(beforeEvents + 1, await EventCountAsync(tenantId, original.Id));

        await AppRealmSeeder.SeedAsync(Factory.Services, tenantId, isControlPlane, ct: ct);
        var repeated = await GetSystemAppAsync(tenantId);
        Assert.Equal(evolved.Permissions, repeated.Permissions);
        Assert.Equal(beforeRoles, await RoleSnapshotAsync(tenantId));
        Assert.Equal(beforeEvents + 1, await EventCountAsync(tenantId, original.Id));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("read", true, false)]
    [InlineData("write", false, true)]
    [InlineData("unrelated", false, false)]
    public async Task Ordinary_cookie_role_enforces_separate_read_and_write_grants(
        string? grant, bool canRead, bool canWrite)
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await GetSystemAppAsync("system");
        var path = $"/api/app/{new ShortGuid(app.Id)}/invite-codes";
        using var seed = await Client.PostAsJsonAsync(path, new { Count = 1 }, ct);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var before = await Client.GetFromJsonAsync<JsonElement>(path, JsonOptions, ct);
        var id = Assert.Single(before.EnumerateArray()).GetProperty("Id").GetString();

        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Invite", "Delegate", "invite-delegate", "invite-delegate@example.test", DefaultPassword);
        if (grant is not null)
        {
            var permission = grant == "unrelated" ? ("user", "read") : ("invite-code", grant);
            // Resolve real seeded catalog IDs: no manual permissions, admin
            // bypass or mocked permission service can conceal a missing entry.
            var role = await Factory.CreateTestRoleAsync("Invite delegate", [permission]);
            Assert.False(role.IsRealmAdmin);
            Assert.Equal(app.Id, role.AppId);
            await Factory.CreateTestGroupAsync("Invite delegates", [user.Id], [role.Id]);
        }
        using var caller = await CreateAuthenticatedClientAsync("invite-delegate", DefaultPassword);

        using var scopedRead = await caller.GetAsync(path, ct);
        using var overview = await caller.GetAsync("/api/admin/invite-codes", ct);
        var expectedRead = canRead ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        Assert.Equal(expectedRead, scopedRead.StatusCode);
        Assert.Equal(expectedRead, overview.StatusCode);
        if (canRead)
        {
            var metadata = await overview.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            var item = Assert.Single(metadata.EnumerateArray());
            Assert.Equal(id, item.GetProperty("Id").GetString());
            Assert.False(item.TryGetProperty("Code", out _));
            Assert.False(item.TryGetProperty("CodeHash", out _));
        }

        using var mint = await caller.PostAsJsonAsync(path, new { Count = 1 }, ct);
        Assert.Equal(canWrite ? HttpStatusCode.OK : HttpStatusCode.Forbidden, mint.StatusCode);
        if (canWrite)
        {
            var body = await mint.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            Assert.Equal(1, body.GetProperty("Codes").GetArrayLength());
        }
        using var revoke = await caller.DeleteAsync($"{path}/{id}", ct);
        Assert.Equal(canWrite ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, revoke.StatusCode);

        // Denied mutations must have no side effects; the writer has replaced
        // the original code with a newly minted one.
        var after = await Client.GetFromJsonAsync<JsonElement>(path, JsonOptions, ct);
        var remainingId = Assert.Single(after.EnumerateArray()).GetProperty("Id").GetString();
        Assert.Equal(!canWrite, id == remainingId);
    }

    [Fact]
    public async Task Anonymous_caller_cannot_read_mint_or_revoke()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await GetSystemAppAsync("system");
        var path = $"/api/app/{new ShortGuid(app.Id)}/invite-codes";
        using var caller = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var read = await caller.GetAsync(path, ct);
        using var overview = await caller.GetAsync("/api/admin/invite-codes", ct);
        using var mint = await caller.PostAsJsonAsync(path, new { Count = 1 }, ct);
        using var revoke = await caller.DeleteAsync($"{path}/{new ShortGuid(Guid.NewGuid())}", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, overview.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mint.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revoke.StatusCode);
    }

    private async Task<string> GetRealmAsync(bool isControlPlane)
    {
        if (isControlPlane) return "system";
        var slug = $"invite-catalog-{Guid.NewGuid():N}"[..23];
        var result = await Factory.Services.GetRequiredService<IRealmProvisioningService>()
            .CreateRealmAsync(new CreateRealmDto
            {
                Slug = slug, DisplayName = "Invite catalog test",
                Domains = [$"{slug}.localhost"], IsActive = false,
            }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        return slug;
    }

    private async Task<App> GetSystemAppAsync(string tenantId)
    {
        await using var session = GetTenantedSession(tenantId);
        var app = await session.Query<App>().FirstOrDefaultAsync(
            a => a.Slug == AppSlugs.Modgud && !a.IsDeleted, TestContext.Current.CancellationToken);
        Assert.NotNull(app);
        return app;
    }

    private async Task<int> EventCountAsync(string tenantId, Guid id)
    {
        await using var session = GetTenantedSession(tenantId);
        return (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Count;
    }

    private async Task<string> RoleSnapshotAsync(string tenantId)
    {
        await using var session = GetTenantedSession(tenantId);
        var roles = await session.Query<PermissionRole>().OrderBy(r => r.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Serialize(roles);
    }

    private static void AssertInviteCatalog(App app)
    {
        var invites = app.Permissions.Where(p => p.Resource == "invite-code").ToList();
        Assert.Equal(2, invites.Count);
        Assert.Single(invites, p => p.Action == "read");
        Assert.Single(invites, p => p.Action == "write");
        Assert.All(invites, p => Assert.NotEqual(Guid.Empty, p.Id));
        Assert.Equal(2, invites.Select(p => p.Id).Distinct().Count());
    }
}
