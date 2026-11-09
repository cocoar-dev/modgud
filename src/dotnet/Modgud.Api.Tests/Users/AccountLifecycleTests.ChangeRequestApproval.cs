using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// B4-AP-02 — approving a profile change request sent only its id: the server applied the
/// request's CURRENT payload, so values the user changed after the admin looked at the request
/// were approved unseen. The approval now carries the state the admin saw and is refused when
/// the request changed since.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task Approving_a_change_request_that_changed_since_it_was_viewed_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Before", lastname: "Review", acronym: $"cr{Guid.NewGuid():N}"[..10], email: $"cr{Guid.NewGuid():N}"[..14] + "@test.com");
        var requestId = Guid.NewGuid();
        var viewedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserChangeRequest
            {
                Id = requestId, UserId = user.Id, Type = ChangeRequestType.Profile,
                Status = ChangeRequestStatus.AdminApprovalPending,
                Payload = """{"Firstname":"Shown"}""", RequestedAt = viewedAt, UpdatedAt = viewedAt,
            });
            await seed.SaveChangesAsync(ct);
        }
        using (var list = await Client.GetAsync("/api/admin/change-requests", ct))
            Assert.True(list.IsSuccessStatusCode);

        // The user edits the pending request after the admin opened it.
        await using (var edit = GetTenantedDocumentSession())
        {
            var cr = (await edit.LoadAsync<UserChangeRequest>(requestId, ct))!;
            cr.Payload = """{"Firstname":"Swapped"}""";
            cr.UpdatedAt = DateTimeOffset.UtcNow;
            edit.Store(cr);
            await edit.SaveChangesAsync(ct);
        }

        using var approve = await Client.PostAsJsonAsync($"/api/admin/change-requests/{new ShortGuid(requestId)}/approve",
            new { NotifyUser = false, ExpectedUpdatedAt = viewedAt }, ct);
        var body = await approve.Content.ReadAsStringAsync(ct);
        Assert.True(approve.StatusCode == HttpStatusCode.Conflict,
            $"a request changed since it was viewed must not be approved, got {(int)approve.StatusCode}: {body}");

        await using var query = GetTenantedSession();
        Assert.Equal("Before", (await query.LoadAsync<ApplicationUser>(user.Id, ct))!.Firstname);
    }
}
