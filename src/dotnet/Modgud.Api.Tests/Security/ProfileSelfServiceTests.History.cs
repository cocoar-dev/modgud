using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B4-AP-05 — a change request's "old" values were read from the user at display time, so
/// after approval the history and the admin list showed the new value as old ("New → New").
/// The values the approval replaced are kept on the request.
/// </summary>
public partial class ProfileSelfServiceTests
{
    [Fact]
    public async Task After_approval_the_change_shows_the_value_it_replaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Before", lastname: "History", acronym: "bh",
            email: "bh@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("bh", "TestPass1234");
        await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "After" }, ct);
        var requestId = await GetOpenRequestIdAsync(user.Id);

        var approve = await Client.PostAsJsonAsync($"/api/admin/change-requests/{requestId}/approve",
            new { NotifyUser = false }, ct);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        using var list = JsonDocument.Parse(await Client.GetStringAsync("/api/admin/change-requests?includeTerminal=true", ct));
        var item = list.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == requestId);
        var change = item.GetProperty("Changes").EnumerateArray().Single(c => c.GetProperty("Field").GetString() == "Firstname");
        Assert.Equal("Before", change.GetProperty("OldValue").GetString());
        Assert.Equal("After", change.GetProperty("NewValue").GetString());
    }
}
