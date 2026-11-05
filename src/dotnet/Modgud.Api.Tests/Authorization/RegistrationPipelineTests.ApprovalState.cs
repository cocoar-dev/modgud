using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.SelfRegistration;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// AID-02 / AREG-03 — with admin approval required, registration created the account
/// inactive, but appended no deactivation: the person and the admin user view projected it as
/// active, so an unapproved account showed (and was looked up) as active. All three now agree
/// until an admin activates the account.
/// </summary>
public partial class RegistrationPipelineTests
{
    [Fact]
    public async Task An_account_awaiting_approval_is_inactive_in_every_read_model()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = CreateTenantScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
            var patched = await settings.PatchAsync(new UpdateRealmSettingsDto
            {
                SelfRegistration = new UpdateSelfRegistrationDto
                {
                    Enabled = true,
                    RequireEmailVerification = false,
                    RequireAdminApproval = true,
                    DefaultGroupIds = [],
                },
            }, ct);
            Assert.False(patched.IsError);
        }

        const string email = "aid02-approval@example.test";
        var register = await Client.PostAsJsonAsync("/api/account/register",
            new RegisterDto { UserName = "aid02approval", Email = email, Password = Password, AcceptedTerms = true },
            JsonOptions, ct);
        register.EnsureSuccessStatusCode();
        var user = await QueryUserByEmailAsync(email);
        Assert.NotNull(user);
        Assert.False(user!.IsActive);
        await Factory.WaitForProjectionsAsync();

        await using var session = GetTenantedSession();
        Assert.False((await session.LoadAsync<Person>(user.Id, ct))!.IsActive, "the person must not be active before approval");
        Assert.False((await session.LoadAsync<UserView>(user.Id, ct))!.IsActive, "the user view must not be active before approval");
    }
}
