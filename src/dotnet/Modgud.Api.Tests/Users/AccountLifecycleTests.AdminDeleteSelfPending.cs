using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Gdpr;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// A.gdpr-C002 — an admin delete skipped every pending deletion regardless of who asked
/// for it. For an account with a pending self-service request it returned success but did
/// not deactivate it or revoke its access: the documented kill switch had no effect, and
/// the user stayed signed in. The admin delete now cuts the account off (revoke, end
/// staffing, deactivate) and keeps the user's own erasure date; an admin restore brings
/// it back.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task AdminDelete_of_a_self_service_pending_account_cuts_it_off_and_restore_reactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, client) = await CreateUserWithClientAsync("SPD");
        var id = new ShortGuid(user.Id).ToString();

        (await client.PostAsJsonAsync("/api/auth/delete-account", new { Password }, JsonOptions, ct)).EnsureSuccessStatusCode();
        DateTimeOffset? selfDeadline;
        await using (var query = GetTenantedSession())
            selfDeadline = (await query.LoadAsync<UserDeletionState>(user.Id, ct))!.DeletionConfirmationDeadline;

        var del = await Client.DeleteAsync($"/api/user/{id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await Factory.WaitForProjectionsAsync();

        // Cut off: deactivated, and the live browser session no longer works.
        await using (var query = GetTenantedSession())
        {
            var appUser = await query.LoadAsync<ApplicationUser>(user.Id, ct);
            Assert.False(appUser!.IsActive);
            var state = await query.LoadAsync<UserDeletionState>(user.Id, ct);
            Assert.True(state!.IsDeletionPending);
            Assert.Equal(DeletionInitiator.SelfService, state.DeletionInitiator);
            Assert.Equal(selfDeadline, state.DeletionConfirmationDeadline);
        }
        using (var me = await client.GetAsync("/api/account/me", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        // Restore brings the account back.
        (await Client.PostAsync($"/api/user/{id}/restore", null, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();
        var restored = await Client.GetFromJsonAsync<UserDto>($"/api/user/{id}", JsonOptions, ct);
        Assert.False(restored!.IsDeletionPending);
        Assert.True(restored.IsActive);
    }
}
