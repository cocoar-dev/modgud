using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.Inbox;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Inbox;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B3-EI-04 — verifying the email of a change request, and approving or rejecting one, committed
/// the new status first and then sent the mail; the inbox items (the admins' "needs review", the
/// requester's outcome) and the dismissal of the admins' items came after the mail. A failing
/// transport failed the request, left the inbox steps undone, and a repeat was refused by the
/// status check. Mail, inbox and dismissal are now follow-ups written in the commit of the status
/// change, each retried on its own.
/// </summary>
public partial class ProfileSelfServiceTests
{
    [Fact]
    public async Task A_failing_admin_mail_after_the_email_was_verified_is_retried_and_the_admins_are_notified()
    {
        var ct = TestContext.Current.CancellationToken;
        var mails = Factory.Services.GetRequiredService<InMemoryEmailService>();
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Verify", lastname: "Mail", acronym: "VM", email: "vm-old@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("vm", "TestPass1234");
        using (var put = await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "vm-new@test.com" }, ct))
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(ct));
        var token = ExtractTokenFromLink(mails.GetLastEmailTo("vm-new@test.com")!.HtmlBody);
        var requestId = await GetOpenRequestIdAsync(user.Id);
        var start = DateTimeOffset.UtcNow;

        Factory.EmailFaults.FailTemplate = EmailTemplate.AdminChangeRequestNotification;
        Factory.EmailFaults.FailTimes = 2;
        using var verify = await Factory.CreateClient().PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = requestId, Token = token }, ct);
        Assert.True(verify.IsSuccessStatusCode,
            $"verifying must not fail on a mail that runs after its commit, got {(int)verify.StatusCode}: {await verify.Content.ReadAsStringAsync(ct)}");

        var request = ShortGuid.Decode(requestId);
        await WaitUntilAsync(async () =>
        {
            await using var query = GetTenantedSession();
            var notified = await query.Query<InboxSourceSlot>()
                .AnyAsync(s => s.SourceType == "change-request" && s.SourceId == request, ct);
            var mailed = mails.GetSentEmails().Any(m => m.SentAt >= start && m.HtmlBody.Contains("/admin/change-requests"));
            return notified && mailed;
        }, "the admins got neither the inbox item nor, after the retries, the mail", ct);
        Assert.Equal(0, Factory.EmailFaults.FailTimes);
    }

    [Fact]
    public async Task A_failing_outcome_mail_after_an_approval_is_retried_and_the_inbox_steps_still_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var mails = Factory.Services.GetRequiredService<InMemoryEmailService>();
        var email = $"om{Guid.NewGuid():N}"[..14] + "@test.com";
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Outcome", lastname: "Mail", acronym: $"om{Guid.NewGuid():N}"[..10], email: email);
        var requestId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        IReadOnlyList<Guid> adminItems;
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            adminItems = await scope.ServiceProvider.GetRequiredService<IInboxNotifier>().NotifyAsync(
                InboxKind.AdminChangeRequestSubmitted, [DefaultUser!.Id], "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = "Outcome Mail", FieldList = "Firstname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: requestId, ct: ct);
            session.Store(new UserChangeRequest
            {
                Id = requestId, UserId = user.Id, Type = ChangeRequestType.Profile,
                Status = ChangeRequestStatus.AdminApprovalPending, Payload = """{"Firstname":"Approved"}""",
                RequestedAt = updatedAt, UpdatedAt = updatedAt, AdminInboxItemIds = adminItems.ToList(),
            });
            await session.SaveChangesAsync(ct);
        }
        var start = DateTimeOffset.UtcNow;

        Factory.EmailFaults.FailTemplate = EmailTemplate.ChangeRequestApproved;
        Factory.EmailFaults.FailTimes = 2;
        using var approve = await Client.PostAsJsonAsync($"/api/admin/change-requests/{new ShortGuid(requestId)}/approve",
            new { NotifyUser = true, ExpectedUpdatedAt = updatedAt }, ct);
        Assert.True(approve.IsSuccessStatusCode,
            $"the approval must not fail on a mail that runs after its commit, got {(int)approve.StatusCode}: {await approve.Content.ReadAsStringAsync(ct)}");

        await WaitUntilAsync(async () =>
        {
            if (!mails.GetSentEmails().Any(m => m.SentAt >= start && string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)))
                return false;
            await Factory.WaitForProjectionsAsync();
            await using var query = GetTenantedSession();
            var outcome = await query.Query<InboxItemView>()
                .AnyAsync(i => i.RecipientUserId == user.Id && i.Kind == InboxKind.ChangeRequestApproved && i.SourceId == requestId, ct);
            var adminItem = await query.LoadAsync<InboxItemView>(adminItems[0], ct);
            return outcome && adminItem?.DismissedAt is not null;
        }, "the requester's mail or inbox item is missing, or the admins' item was not dismissed", ct);
        Assert.Equal(0, Factory.EmailFaults.FailTimes);
    }

    /// <summary>A verification mail that failed on submit was never sent again: the next submit
    /// of the same address found the token hash and generated no new token and no mail.</summary>
    [Fact]
    public async Task A_failed_verification_mail_is_sent_again_on_the_next_submit()
    {
        var ct = TestContext.Current.CancellationToken;
        var mails = Factory.Services.GetRequiredService<InMemoryEmailService>();
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Resend", lastname: "Mail", acronym: "RM", email: "rm-old@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("rm", "TestPass1234");

        Factory.EmailFaults.FailTemplate = EmailTemplate.EmailVerification;
        Factory.EmailFaults.FailTimes = 1;
        using (await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "rm-new@test.com" }, ct)) { }
        Assert.Null(mails.GetLastEmailTo("rm-new@test.com"));

        using (var again = await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "rm-new@test.com" }, ct))
            Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync(ct));
        var mail = mails.GetLastEmailTo("rm-new@test.com");
        Assert.NotNull(mail);

        using var verify = await Factory.CreateClient().PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = await GetOpenRequestIdAsync(user.Id), Token = ExtractTokenFromLink(mail!.HtmlBody) }, ct);
        Assert.True(verify.IsSuccessStatusCode, await verify.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A submit that reverts every pending change deletes the request; the admins'
    /// "needs review" items stayed open for a request that no longer existed.</summary>
    [Fact]
    public async Task A_submit_that_reverts_every_change_dismisses_the_admins_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Revert", lastname: "Me", acronym: "RV", email: "rv@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("rv", "TestPass1234");
        using (var put = await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "Changed" }, ct))
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(ct));
        var request = ShortGuid.Decode(await GetOpenRequestIdAsync(user.Id));

        Guid adminItem = Guid.Empty;
        await WaitUntilAsync(async () =>
        {
            await using var query = GetTenantedSession();
            var slot = await query.Query<InboxSourceSlot>()
                .FirstOrDefaultAsync(s => s.SourceType == "change-request" && s.SourceId == request, ct);
            adminItem = slot?.OpenItemId ?? Guid.Empty;
            return slot is not null;
        }, "the admins got no inbox item for the request", ct);

        using (var revert = await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "Revert" }, ct))
            Assert.True(revert.IsSuccessStatusCode, await revert.Content.ReadAsStringAsync(ct));

        await WaitUntilAsync(async () =>
        {
            await Factory.WaitForProjectionsAsync();
            await using var query = GetTenantedSession();
            return (await query.LoadAsync<InboxItemView>(adminItem, ct))?.DismissedAt is not null;
        }, "the admins' item of the deleted request stayed open", ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(250, ct);
        }
    }
}
