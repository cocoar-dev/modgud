using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// AID-03 — <c>PUT /api/user/{id}/active</c> reactivated an account in the recycle bin
/// although every other edit is frozen while a deletion is pending ("restore first").
/// AID-04 — <c>PUT /api/user/{id}/password</c> removed the old password before the new one
/// was validated, so a rejected new password left the account without a password.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task A_binned_account_cannot_be_reactivated_without_a_restore()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Bin", lastname: "Ned", acronym: $"bin{Guid.NewGuid():N}"[..10], email: $"bin{Guid.NewGuid():N}@test.com"[..20]);
        var id = new ShortGuid(user.Id);

        using (var binned = await Client.DeleteAsync($"/api/user/{id}", ct))
            Assert.True(binned.IsSuccessStatusCode, await binned.Content.ReadAsStringAsync(ct));

        using var activate = await Client.PutAsJsonAsync($"/api/user/{id}/active", new { IsActive = true }, ct);
        var body = await activate.Content.ReadAsStringAsync(ct);
        Assert.True(activate.StatusCode == HttpStatusCode.Conflict,
            $"a binned account must be restored, not reactivated, got {(int)activate.StatusCode}: {body}");
    }

    [Fact]
    public async Task A_rejected_new_password_keeps_the_old_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var userName = $"pwd{Guid.NewGuid():N}"[..12];
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Pass", lastname: "Word", acronym: userName, email: $"{userName}@test.com",
            password: DefaultPassword, isRealmAdmin: false);

        using (var rejected = await Client.PutAsJsonAsync($"/api/user/{new ShortGuid(user.Id)}/password", new { Password = "x" }, ct))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // The old password still signs in (the helper asserts a successful login).
        using var stillWorks = await CreateAuthenticatedClientAsync(userName, DefaultPassword);
        using var me = await stillWorks.GetAsync("/api/account/me", ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }
}
