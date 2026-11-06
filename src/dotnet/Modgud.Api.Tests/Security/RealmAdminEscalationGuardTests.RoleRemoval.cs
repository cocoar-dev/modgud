using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// D.roles-F03 — revoking realm:admin is as sensitive as conferring it. A delegated
/// <c>permission-role:write</c> holder must neither delete a realm-admin role nor lower its
/// flag; only a realm administrator may.
/// </summary>
public partial class RealmAdminEscalationGuardTests
{
    [Fact]
    public async Task RoleDelete_OfRealmAdminRole_AsNonRealmAdmin_Is403()
    {
        var (client, _) = await CreateDelegatedAdminClientAsync("rd", ("permission-role", "write"));
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);

        var res = await client.DeleteAsync($"/api/role/{new ShortGuid(realmAdminRole.Id)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("Role.RealmAdminForbidden", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RoleUpdate_LoweringRealmAdminFlag_AsNonRealmAdmin_Is403()
    {
        var (client, _) = await CreateDelegatedAdminClientAsync("rl", ("permission-role", "write"));
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);

        var res = await client.PutAsJsonAsync($"/api/role/{new ShortGuid(realmAdminRole.Id)}", new
        {
            Name = realmAdminRole.Name,
            IsRealmAdmin = false,
            PermissionIds = Array.Empty<string>(),
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("Role.RealmAdminForbidden", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RoleDelete_OfRealmAdminRole_AsRealmAdmin_Succeeds_While_Another_Remains()
    {
        // Positive control: a realm admin may still remove a realm-admin role that is not the last.
        var realmAdminRole = await Factory.CreateTestRoleAsync($"RA_{Guid.NewGuid():N}", isRealmAdmin: true);

        var res = await Client.DeleteAsync($"/api/role/{new ShortGuid(realmAdminRole.Id)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }
}
