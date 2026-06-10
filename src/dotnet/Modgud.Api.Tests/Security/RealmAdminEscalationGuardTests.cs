using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Privilege-escalation guard (audit H1): "only a realm:admin may confer
/// realm:admin." A delegated <c>permission-role:write</c> /
/// <c>authorization-group:write</c> / <c>app:write</c> grant must NOT be a path
/// to the realm-wide bypass. Each test drives the real endpoint as a non-admin
/// who legitimately holds the relevant write permission, and asserts the escape
/// is blocked — while the positive controls prove a real realm:admin is
/// unaffected.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Security")]
public class RealmAdminEscalationGuardTests : IntegrationTestBase
{
    public RealmAdminEscalationGuardTests(SharedPostgresFixture fixture) : base(fixture) { }

    /// <summary>
    /// Creates a non-admin user holding exactly <paramref name="permissions"/>
    /// (modgud-app catalog grants) via a dedicated role+group, and returns an
    /// authenticated client plus the user id.
    /// </summary>
    private async Task<(HttpClient Client, Guid UserId)> CreateDelegatedAdminClientAsync(
        string userName, params (string Resource, string Action)[] permissions)
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: userName, lastname: "Delegate", acronym: userName,
            email: $"{userName}@test.com", password: "TestPass1234", isRealmAdmin: false);

        var role = await Factory.CreateTestRoleAsync(
            $"Delegated_{userName}", permissions: permissions);
        await Factory.CreateTestGroupAsync(
            $"DelegatedGroup_{userName}", memberIds: [user.Id], roleIds: [role.Id]);

        var client = await CreateAuthenticatedClientAsync(userName.ToLowerInvariant(), "TestPass1234");
        return (client, user.Id);
    }

    [Fact]
    public async Task RoleCreate_WithIsRealmAdmin_AsNonRealmAdmin_Is403()
    {
        var (client, _) = await CreateDelegatedAdminClientAsync("rw", ("permission-role", "write"));

        var res = await client.PostAsJsonAsync("/api/role", new
        {
            Name = "Sneaky Admin Role",
            IsRealmAdmin = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Role.RealmAdminForbidden", body);
    }

    [Fact]
    public async Task RoleCreate_WithIsRealmAdmin_AsRealmAdmin_Succeeds()
    {
        // Positive control: the default Client is a realm admin → allowed.
        var res = await Client.PostAsJsonAsync("/api/role", new
        {
            Name = "Legit Admin Role",
            IsRealmAdmin = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task GroupCreate_AttachingRealmAdminRole_AsNonRealmAdmin_Is403()
    {
        var (client, _) = await CreateDelegatedAdminClientAsync("gw", ("authorization-group", "write"));
        var realmAdminRole = await Factory.CreateTestRoleAsync(
            $"RA_{Guid.NewGuid():N}", isRealmAdmin: true);

        var res = await client.PostAsJsonAsync("/api/group", new
        {
            Name = "Sneaky Admin Group",
            MemberIds = Array.Empty<string>(),
            RoleIds = new[] { new ShortGuid(realmAdminRole.Id).ToString() },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Group.RealmAdminConferralForbidden", body);
    }

    [Fact]
    public async Task GroupUpdate_AddingSelfToRealmAdminGroup_AsNonRealmAdmin_Is403()
    {
        // The "add myself to System Admins" vector.
        var (client, attackerId) = await CreateDelegatedAdminClientAsync("mw", ("authorization-group", "write"));
        var realmAdminRole = await Factory.CreateTestRoleAsync(
            $"RA_{Guid.NewGuid():N}", isRealmAdmin: true);
        var adminGroup = await Factory.CreateTestGroupAsync(
            $"RealmAdmins_{Guid.NewGuid():N}", memberIds: [], roleIds: [realmAdminRole.Id]);

        var res = await client.PutAsJsonAsync($"/api/group/{new ShortGuid(adminGroup.Id)}", new
        {
            Name = adminGroup.Name,
            MemberIds = new[] { new ShortGuid(attackerId).ToString() },
            RoleIds = new[] { new ShortGuid(realmAdminRole.Id).ToString() },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Group.RealmAdminMembershipForbidden", body);

        // Fail-closed: the attacker was NOT added.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var reloaded = await session.LoadAsync<Group>(adminGroup.Id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(attackerId, reloaded!.MemberIds);
    }

    [Fact]
    public async Task AppCatalog_RejectsLiteralRealmAdminPermission()
    {
        // Vector 3: an app:write holder injecting a literal realm:admin catalog
        // entry. Driven as the realm-admin Client — the point is the catalog
        // reservation itself, which fires regardless of caller.
        var res = await Client.PostAsJsonAsync("/api/app", new
        {
            Slug = "acme-escalate",
            DisplayName = "Acme Escalate",
            Description = (string?)null,
            Permissions = new[]
            {
                new { Id = (string?)null, Resource = "realm", Action = "admin", Description = (string?)null },
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("App.ReservedPermission", body);
    }
}
