using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Setup;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// English-naming pass (2026-07): the seeded realm-admin group is now named
/// <see cref="AdminGroupNames.Current"/> ("Administrators") instead of the
/// legacy <see cref="AdminGroupNames.Legacy"/> ("Administratoren"). These tests
/// cover the three angles that make the rename safe for realms that already
/// existed under the old name: (a) fresh realms seed the new name, (b) the
/// bootstrapper joins a pre-existing legacy group instead of duplicating it,
/// and (c) the boot-time migration renames a legacy group in place, idempotently.
/// </summary>
public class AdminGroupRenameTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Bootstrap_admin_on_a_fresh_realm_seeds_a_group_named_administrators()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var result = await CliHarness.RunAsync(host.Services,
            "bootstrap-admin", "--username", "freshadmin", "--email", "freshadmin@cli.local",
            "--password", "TestPass1234");

        Assert.Equal(0, result.ExitCode);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("system");

        var groups = await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct);
        var group = Assert.Single(groups);
        Assert.Equal(AdminGroupNames.Current, group.Name);

        var adminUser = await session.Query<ApplicationUser>()
            .SingleAsync(u => u.UserName == "freshadmin", ct);
        Assert.Contains(adminUser.Id, group.MemberIds);
    }

    [Fact]
    public async Task Bootstrap_admin_joins_a_pre_existing_legacy_group_instead_of_duplicating_it()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // Simulate a realm that already carries a legacy-named group with no
        // role attached yet — e.g. imported by a manifest that predates role
        // wiring. This is the exact edge case the name-based fallback lookup
        // in RealmAdminBootstrapper exists for.
        var legacyGroup = await host.Factory.CreateTestGroupAsync(
            AdminGroupNames.Legacy, memberIds: [], roleIds: [], boundTo: ["*"]);

        var result = await CliHarness.RunAsync(host.Services,
            "bootstrap-admin", "--username", "legacyadmin", "--email", "legacyadmin@cli.local",
            "--password", "TestPass1234");

        Assert.Equal(0, result.ExitCode);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("system");

        // No duplicate group — the pre-existing legacy group is the only one.
        var groups = await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct);
        var group = Assert.Single(groups);
        Assert.Equal(legacyGroup.Id, group.Id);
        // The bootstrapper joins the group; it doesn't rename it — that's the
        // migration's job (see the rename tests below).
        Assert.Equal(AdminGroupNames.Legacy, group.Name);

        var adminUser = await session.Query<ApplicationUser>()
            .SingleAsync(u => u.UserName == "legacyadmin", ct);
        Assert.Contains(adminUser.Id, group.MemberIds);

        var adminRole = await session.Query<PermissionRole>().SingleAsync(r => r.IsRealmAdmin, ct);
        Assert.Contains(adminRole.Id, group.RoleIds);
    }

    [Fact]
    public async Task Legacy_admin_group_rename_migration_renames_in_place_and_is_idempotent()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // The boot-time migration already ran once during host startup above,
        // against an empty tenant — a no-op. Seed the legacy group AFTER boot,
        // then re-run the same hosted-service instance to exercise a "next
        // boot" that actually finds something to rename.
        var legacyGroup = await host.Factory.CreateTestGroupAsync(
            AdminGroupNames.Legacy,
            memberIds: [Guid.NewGuid()],
            roleIds: [Guid.NewGuid()],
            description: "Full system access",
            boundTo: ["*"]);

        var migration = host.Services.GetServices<IHostedService>()
            .OfType<LegacyAdminGroupRenameBootstrap>()
            .Single();

        await migration.StartAsync(ct);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.QuerySession("system"))
        {
            var renamed = await session.LoadAsync<Group>(legacyGroup.Id, ct);
            Assert.NotNull(renamed);
            Assert.Equal(AdminGroupNames.Current, renamed!.Name);
            // Every other field carries over untouched.
            Assert.Equal(legacyGroup.MemberIds, renamed.MemberIds);
            Assert.Equal(legacyGroup.RoleIds, renamed.RoleIds);
            Assert.Equal(legacyGroup.Description, renamed.Description);
            Assert.Equal(legacyGroup.BoundTo, renamed.BoundTo);
        }

        // Second run against already-renamed data — idempotent no-op, no
        // duplicate and no error.
        await migration.StartAsync(ct);

        await using (var session = store.QuerySession("system"))
        {
            var groups = await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct);
            var group = Assert.Single(groups);
            Assert.Equal(AdminGroupNames.Current, group.Name);
        }
    }
}
