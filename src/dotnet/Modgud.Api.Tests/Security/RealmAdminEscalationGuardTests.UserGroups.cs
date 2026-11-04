using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// k692 — the user-side membership routes changed group membership with only
/// <c>user:write</c>, outside the "only a realm:admin may confer realm:admin" guard of the
/// group commands: a delegated user administrator could add themselves (or a fresh account they
/// created) to an admin group, or remove administrators from it.
/// </summary>
public partial class RealmAdminEscalationGuardTests
{
    [Fact]
    public async Task UserAddGroup_AddingSelfToRealmAdminGroup_AsUserWriter_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, attackerId) = await CreateDelegatedAdminClientAsync("ua", ("user", "write"));
        var admins = await CreateRealmAdminGroupAsync(memberIds: []);

        var res = await client.PostAsJsonAsync($"/api/user/{new ShortGuid(attackerId)}/groups",
            new { GroupId = new ShortGuid(admins.Id).ToString() }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("Group.RealmAdminMembershipForbidden", await res.Content.ReadAsStringAsync(ct));
        Assert.DoesNotContain(attackerId, (await ReloadGroupAsync(admins.Id))!.MemberIds);
    }

    [Fact]
    public async Task UserRemoveGroup_RemovingAnAdminFromRealmAdminGroup_AsUserWriter_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, _) = await CreateDelegatedAdminClientAsync("ur", ("user", "write"));
        var admin = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Kept", lastname: "Admin", acronym: "KA", email: "kept-admin@test.com", password: "TestPass1234");
        var admins = await CreateRealmAdminGroupAsync(memberIds: [admin.Id]);

        var res = await client.DeleteAsync($"/api/user/{new ShortGuid(admin.Id)}/groups/{new ShortGuid(admins.Id)}", ct);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains(admin.Id, (await ReloadGroupAsync(admins.Id))!.MemberIds);
    }

    [Fact]
    public async Task UserCreate_IntoRealmAdminGroup_AsUserWriter_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, _) = await CreateDelegatedAdminClientAsync("uc", ("user", "write"));
        var admins = await CreateRealmAdminGroupAsync(memberIds: []);

        var res = await client.PostAsJsonAsync("/api/user", new
        {
            Firstname = "Planted", Lastname = "Admin", Acronym = "PA",
            Email = "planted-admin@test.com", UserName = "plantedadmin", Password = "TestPass1234",
            GroupIds = new[] { new ShortGuid(admins.Id).ToString() },
        }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty((await ReloadGroupAsync(admins.Id))!.MemberIds);
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        Assert.False(await session.Query<Person>().AnyAsync(p => p.Email == "planted-admin@test.com", ct));
    }

    [Fact]
    public async Task UserAddGroup_OrdinaryGroup_AsUserWriter_And_RealmAdminGroup_AsRealmAdmin_Succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, writerId) = await CreateDelegatedAdminClientAsync("uo", ("user", "write"));
        var ordinary = await Factory.CreateTestGroupAsync($"Ordinary_{Guid.NewGuid():N}", memberIds: []);
        var admins = await CreateRealmAdminGroupAsync(memberIds: []);

        var own = await client.PostAsJsonAsync($"/api/user/{new ShortGuid(writerId)}/groups",
            new { GroupId = new ShortGuid(ordinary.Id).ToString() }, ct);
        var byAdmin = await Client.PostAsJsonAsync($"/api/user/{new ShortGuid(writerId)}/groups",
            new { GroupId = new ShortGuid(admins.Id).ToString() }, ct);

        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byAdmin.StatusCode);
        Assert.Contains(writerId, (await ReloadGroupAsync(admins.Id))!.MemberIds);
    }

    private async Task<Group> CreateRealmAdminGroupAsync(List<Guid> memberIds)
    {
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);
        return await Factory.CreateTestGroupAsync(
            $"Admins_{Guid.NewGuid():N}", memberIds: memberIds, roleIds: [realmAdminRole.Id]);
    }
}
