using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B3-F01 — <c>BoundTo</c> decides in which apps a group's roles take effect. A group that
/// already holds a realm:admin role but is bound nowhere is dormant; binding it to Modgud makes
/// its members realm administrators. The conferral guard only looked at newly added roles and
/// membership changes, so a delegated group writer could activate (or switch off) realm:admin
/// for the group's existing members by changing <c>BoundTo</c> alone.
/// </summary>
public partial class RealmAdminEscalationGuardTests
{
    [Fact]
    public async Task GroupUpdate_BindingADormantRealmAdminGroup_AsNonRealmAdmin_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, attackerId) = await CreateDelegatedAdminClientAsync("bw", ("authorization-group", "write"));
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);
        var dormant = await Factory.CreateTestGroupAsync(
            $"DormantAdmins_{Guid.NewGuid():N}", memberIds: [attackerId], roleIds: [realmAdminRole.Id], boundTo: []);

        var res = await client.PutAsJsonAsync($"/api/group/{new ShortGuid(dormant.Id)}", new
        {
            Name = dormant.Name,
            MemberIds = new[] { new ShortGuid(attackerId).ToString() },
            RoleIds = new[] { new ShortGuid(realmAdminRole.Id).ToString() },
            BoundTo = new[] { AppSlugs.Modgud },
        }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("Group.RealmAdminActivationForbidden", await res.Content.ReadAsStringAsync(ct));
        Assert.Empty((await ReloadGroupAsync(dormant.Id))!.BoundTo);
    }

    [Fact]
    public async Task GroupUpdate_UnbindingARealmAdminGroup_AsNonRealmAdmin_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, _) = await CreateDelegatedAdminClientAsync("uw", ("authorization-group", "write"));
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);
        var admins = await Factory.CreateTestGroupAsync(
            $"Admins_{Guid.NewGuid():N}", memberIds: [], roleIds: [realmAdminRole.Id]);

        var res = await client.PutAsJsonAsync($"/api/group/{new ShortGuid(admins.Id)}", new
        {
            Name = admins.Name,
            MemberIds = Array.Empty<string>(),
            RoleIds = new[] { new ShortGuid(realmAdminRole.Id).ToString() },
            BoundTo = Array.Empty<string>(),
        }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal([AppSlugs.Modgud], (await ReloadGroupAsync(admins.Id))!.BoundTo);
    }

    [Fact]
    public async Task GroupUpdate_BindingADormantRealmAdminGroup_AsRealmAdmin_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);
        var dormant = await Factory.CreateTestGroupAsync(
            $"DormantAdmins_{Guid.NewGuid():N}", memberIds: [], roleIds: [realmAdminRole.Id], boundTo: []);

        var res = await Client.PutAsJsonAsync($"/api/group/{new ShortGuid(dormant.Id)}", new
        {
            Name = dormant.Name,
            MemberIds = Array.Empty<string>(),
            RoleIds = new[] { new ShortGuid(realmAdminRole.Id).ToString() },
            BoundTo = new[] { AppSlugs.Modgud },
        }, ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal([AppSlugs.Modgud], (await ReloadGroupAsync(dormant.Id))!.BoundTo);
    }

    private async Task<Group?> ReloadGroupAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<Group>(id, TestContext.Current.CancellationToken);
    }
}
