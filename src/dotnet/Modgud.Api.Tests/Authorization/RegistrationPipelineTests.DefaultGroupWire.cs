using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.SelfRegistration;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// AREG-02 — the admin UI stores default group ids as ShortGuids, but registration only
/// accepted Guid text and skipped them silently. AREG-04 — a registration's membership was
/// written straight into the group document, with no group event: a rebuild of the group
/// projection lost it. The membership is now joined from either id form and recorded as a
/// group event, like every other membership change.
/// </summary>
public partial class RegistrationPipelineTests
{
    [Fact]
    public async Task A_default_group_saved_as_short_guid_is_joined_through_a_group_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var role = await Factory.CreateTestRoleAsync("Wire readers", [("user", "read")]);
        var group = await Factory.CreateTestGroupAsync("Wire readers", [], [role.Id]);

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
                    DefaultGroupIds = [new ShortGuid(group.Id).ToString()],
                },
            }, ct);
            Assert.False(patched.IsError);
        }

        const string email = "areg-wire@example.test";
        var register = await Client.PostAsJsonAsync("/api/account/register",
            new RegisterDto { UserName = "aregwire", Email = email, Password = Password, AcceptedTerms = true },
            JsonOptions, ct);
        register.EnsureSuccessStatusCode();
        var user = await QueryUserByEmailAsync(email);
        Assert.NotNull(user);

        await using var session = GetTenantedSession();
        Assert.Contains(user!.Id, (await session.LoadAsync<Group>(group.Id, ct))!.MemberIds);
        var events = await session.Events.FetchStreamAsync(group.Id, token: ct);
        Assert.Contains(events.Select(e => e.Data).OfType<GroupUpdatedEvent>(), e => e.MemberIds.Contains(user.Id));
    }
}
