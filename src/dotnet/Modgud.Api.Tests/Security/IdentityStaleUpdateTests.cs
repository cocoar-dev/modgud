using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// AID-01 — <c>EventSourcedUserStore.UpdateAsync</c> wrote the whole <see cref="ApplicationUser"/>
/// it was given and patched password hash, security stamp and factor fields from it, without
/// comparing the concurrency stamp it was loaded with. An identity update that loaded the user
/// before a deactivation or a password change restored the earlier state when it saved. It now
/// writes only what it changed itself, so the later state stays.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class IdentityStaleUpdateTests : IntegrationTestBase
{
    public IdentityStaleUpdateTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string OldPassword = "TestPass1234";
    private const string NewPassword = "NewPass56789";

    [Fact]
    public async Task An_update_loaded_before_a_deactivation_does_not_reactivate_the_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Stale", lastname: "Active", acronym: "SA", email: "stale-active@test.com", password: OldPassword);

        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var stale = Factory.Services.CreateScope();
        var staleManager = stale.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var staleUser = (await staleManager.FindByIdAsync(created.Id.ToString()))!;

        var deactivate = await Client.PutAsJsonAsync($"/api/user/{new ShortGuid(created.Id)}/active", new { IsActive = false }, ct);
        deactivate.EnsureSuccessStatusCode();

        staleUser.Firstname = "Renamed";
        var result = await staleManager.UpdateAsync(staleUser);

        Assert.True(result.Succeeded);
        await using var read = GetTenantedSession();
        var stored = (await read.LoadAsync<ApplicationUser>(created.Id, ct))!;
        Assert.False(stored.IsActive);
        Assert.Equal("Renamed", stored.Firstname);
    }

    [Fact]
    public async Task An_update_loaded_before_a_password_change_does_not_restore_the_old_password()
    {
        var created = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Stale", lastname: "Password", acronym: "SP", email: "stale-password@test.com", password: OldPassword);

        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var stale = Factory.Services.CreateScope();
        var staleManager = stale.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var staleUser = (await staleManager.FindByIdAsync(created.Id.ToString()))!;

        using (var change = Factory.Services.CreateScope())
        {
            var manager = change.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var fresh = (await manager.FindByIdAsync(created.Id.ToString()))!;
            var changed = await manager.ChangePasswordAsync(fresh, OldPassword, NewPassword);
            Assert.True(changed.Succeeded, string.Join("; ", changed.Errors.Select(e => e.Description)));
        }

        staleUser.Firstname = "Renamed";
        var result = await staleManager.UpdateAsync(staleUser);

        using var check = Factory.Services.CreateScope();
        var checkManager = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var current = (await checkManager.FindByIdAsync(created.Id.ToString()))!;
        Assert.True(result.Succeeded);
        Assert.Equal("Renamed", current.Firstname);
        Assert.True(await checkManager.CheckPasswordAsync(current, NewPassword));
        Assert.False(await checkManager.CheckPasswordAsync(current, OldPassword));
    }
}
