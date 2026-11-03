using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.SelfRegistration;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Permissions;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// AREG-01 — a self-registration's default groups were joined without the conferral
/// guard that protects every other membership change. A delegated realm-settings writer
/// (no realm:admin) could name a realm-admin group as a default, register a fresh account
/// and hold realm:admin through it. Registering never confers realm:admin, whoever
/// configured the defaults; ordinary default groups still apply.
/// </summary>
public partial class RegistrationPipelineTests
{
    [Fact]
    public async Task Self_registration_never_makes_the_new_account_a_realm_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminRole = await Factory.CreateTestRoleAsync("Registration admins", isRealmAdmin: true);
        var adminGroup = await Factory.CreateTestGroupAsync("Registration admins", [], [adminRole.Id], boundTo: ["*"]);
        var readerRole = await Factory.CreateTestRoleAsync("Registration readers", [("user", "read")]);
        var readerGroup = await Factory.CreateTestGroupAsync("Registration readers", [], [readerRole.Id]);
        // Child of the admin group: realm:admin flows down to its members as well.
        var childGroup = await Factory.CreateTestGroupAsync("Registration admin child", [], []);
        await AddGroupMemberAsync(adminGroup.Id, childGroup.Id);

        using (var scope = CreateTenantScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
            var patched = await settings.PatchAsync(new UpdateRealmSettingsDto
            {
                SelfRegistration = new UpdateSelfRegistrationDto
                {
                    Enabled = true,
                    RequireEmailVerification = false,
                    RequireAdminApproval = false,
                    DefaultGroupIds = [adminGroup.Id.ToString(), childGroup.Id.ToString(), readerGroup.Id.ToString()],
                },
            }, ct);
            Assert.False(patched.IsError);
        }

        const string email = "areg-default-groups@example.test";
        var register = await Client.PostAsJsonAsync("/api/account/register",
            new RegisterDto { UserName = "aregdefaults", Email = email, Password = Password, AcceptedTerms = true },
            JsonOptions, ct);
        register.EnsureSuccessStatusCode();
        var user = await QueryUserByEmailAsync(email);
        Assert.NotNull(user);

        await using (var session = GetTenantedSession())
        {
            Assert.DoesNotContain(user!.Id, (await session.LoadAsync<Group>(adminGroup.Id, ct))!.MemberIds);
            Assert.DoesNotContain(user.Id, (await session.LoadAsync<Group>(childGroup.Id, ct))!.MemberIds);
            Assert.Contains(user.Id, (await session.LoadAsync<Group>(readerGroup.Id, ct))!.MemberIds);
        }
        using (var scope = CreateTenantScope())
        {
            var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
            Assert.False(await permissions.HasPermissionAsync(user!.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct));
        }
    }

    private async Task AddGroupMemberAsync(Guid groupId, Guid memberId)
    {
        await using var session = GetTenantedDocumentSession();
        var group = await session.LoadAsync<Group>(groupId, TestContext.Current.CancellationToken);
        group!.MemberIds.Add(memberId);
        session.Store(group);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
