using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Identity;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B3-EI-02 — admin notifications (e-mail with old and new profile values, inbox items) went
/// to every group holding a realm-admin role, whatever its binding. A group whose binding
/// leaves it dormant for the IdP confers no admin right there, so its members are no admin
/// recipients either.
/// </summary>
public sealed class AdminNotifierBindingTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Members_of_a_dormant_admin_group_are_not_notified()
    {
        var ct = TestContext.Current.CancellationToken;
        var dormantMember = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Dormant", lastname: "Admin", acronym: $"da{Guid.NewGuid():N}"[..10],
            email: $"dormant-{Guid.NewGuid():N}"[..20] + "@test.com");
        var role = await Factory.CreateTestRoleAsync($"Dormant admins {Guid.NewGuid():N}", isRealmAdmin: true);
        await Factory.CreateTestGroupAsync($"Dormant admins {Guid.NewGuid():N}", [dormantMember.Id], [role.Id], boundTo: []);

        using var scope = Factory.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IAdminNotifier>();
        Assert.DoesNotContain(dormantMember.Id, await notifier.GetAdminRecipientUserIdsAsync(ct));
        Assert.DoesNotContain(dormantMember.Email, await notifier.GetAdminRecipientsAsync(ct));
    }
}
