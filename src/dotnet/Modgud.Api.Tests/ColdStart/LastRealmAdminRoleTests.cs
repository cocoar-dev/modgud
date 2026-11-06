using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Roles;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Roles;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// D.roles-F03 — the realm must keep at least one realm-admin role: neither deleting the
/// last one nor lowering its flag is allowed, also not for a realm administrator (the
/// lockout would need the recovery CLI). Isolated host, so the bootstrapped admin role is
/// genuinely the only one.
/// </summary>
public class LastRealmAdminRoleTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task The_last_realm_admin_role_can_neither_be_deleted_nor_lowered()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;
        var cli = await CliHarness.RunAsync(host.Services,
            "bootstrap-admin", "--username", "lastadmin", "--email", "lastadmin@cli.local", "--password", "TestPass1234");
        Assert.Equal(0, cli.ExitCode);

        using var tenant = TenantContext.Enter("system");
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleAdminService>();
        var adminRole = Assert.Single(await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted && r.IsRealmAdmin).ToListAsync(ct));

        var lowered = await roles.UpdateRoleAsync(adminRole.Id,
            new RolePayload(adminRole.Name, adminRole.Description, null, false, []), callerIsRealmAdmin: true, ct);
        Assert.True(lowered.IsError);
        Assert.Equal("Role.LastRealmAdminRole", lowered.FirstError.Code);

        var deleted = await roles.DeleteRoleAsync(adminRole.Id, callerIsRealmAdmin: true, ct);
        Assert.True(deleted.IsError);
        Assert.Equal("Role.LastRealmAdminRole", deleted.FirstError.Code);
    }
}
