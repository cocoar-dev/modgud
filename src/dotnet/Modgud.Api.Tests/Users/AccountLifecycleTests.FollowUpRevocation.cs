using System.Net.Http.Json;
using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Api.Users;
using Modgud.Authentication.Domain;
using Modgud.Domain.Common;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// B4-AP-01 — approving an email change committed the user first, revoked the user's access
/// afterwards in several commits, and only then marked the request Approved in a commit of its
/// own. A failing revoke left the new email in effect, the request pending and the old access
/// alive; repeating the approval saw an unchanged email and revoked nothing. The user change,
/// the approval and a <c>RevokeUserAccess</c> follow-up are now one commit, and the follow-up is
/// retried until the revoke held.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task A_failing_access_revoke_after_an_approved_email_change_is_completed_by_the_follow_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var newEmail = $"cn{Guid.NewGuid():N}"[..14] + "@test.com";
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Mail", lastname: "Change", acronym: $"mc{Guid.NewGuid():N}"[..10], email: $"co{Guid.NewGuid():N}"[..14] + "@test.com");
        string? stampBefore;
        await using (var query = GetTenantedSession())
            stampBefore = (await query.LoadAsync<ApplicationUser>(user.Id, ct))!.SecurityStamp;

        var requestId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserChangeRequest
            {
                Id = requestId, UserId = user.Id, Type = ChangeRequestType.Profile,
                Status = ChangeRequestStatus.AdminApprovalPending,
                Payload = $$"""{"Email":"{{newEmail}}"}""", RequestedAt = requestedAt, UpdatedAt = requestedAt,
            });
            await seed.SaveChangesAsync(ct);
        }

        Factory.UserAccessRevokerFaults.FailRevokes = 2; // the immediate attempt and the first follow-up attempt
        using var approve = await Client.PostAsJsonAsync($"/api/admin/change-requests/{new ShortGuid(requestId)}/approve",
            new { NotifyUser = false, ExpectedUpdatedAt = requestedAt }, ct);
        Assert.True(approve.IsSuccessStatusCode,
            $"the approval must not fail on a revoke that runs after its commit, got {(int)approve.StatusCode}: {await approve.Content.ReadAsStringAsync(ct)}");

        await using (var query = GetTenantedSession())
        {
            Assert.Equal(ChangeRequestStatus.Approved, (await query.LoadAsync<UserChangeRequest>(requestId, ct))!.Status);
            Assert.Equal(newEmail, (await query.LoadAsync<ApplicationUser>(user.Id, ct))!.Email);
        }

        // The revoke rotates the security stamp — the follow-up does it after the failed attempts.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            await using var query = GetTenantedSession();
            if ((await query.LoadAsync<ApplicationUser>(user.Id, ct))!.SecurityStamp != stampBefore) break;
            Assert.True(DateTime.UtcNow < deadline, "The user's access was not revoked after the email change.");
            await Task.Delay(200, ct);
        }
        Assert.Equal(0, Factory.UserAccessRevokerFaults.FailRevokes);
    }

    /// <summary>The follow-up is the safety net of the immediate revoke: scheduled after it, not run
    /// next to it (both appended to the user's stream at once, and one failed on the version).</summary>
    [Fact]
    public async Task The_access_revoke_follow_up_of_an_email_change_runs_after_the_immediate_revoke()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Safety", lastname: "Net", acronym: $"sn{Guid.NewGuid():N}"[..10], email: $"so{Guid.NewGuid():N}"[..14] + "@test.com");
        var committedBefore = DateTimeOffset.UtcNow;
        using (var scope = Factory.Services.CreateScope())
        {
            var result = await GetTenantedMessageBus(scope).InvokeAsync<ErrorOr<UserDto>>(new UpdateUserCommand(
                user.Id, default, default, default, new Optional<string>($"sn{Guid.NewGuid():N}"[..14] + "@test.com"), default), ct);
            Assert.False(result.IsError);
        }

        // Two revokes: the immediate one, then the follow-up once it is due — never at the same time.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        List<(Guid UserId, DateTimeOffset Started, DateTimeOffset Ended)> calls;
        while ((calls = Factory.UserAccessRevokerFaults.Calls.Where(c => c.UserId == user.Id).OrderBy(c => c.Started).ToList()).Count < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The access revoke follow-up did not run ({calls.Count} revoke(s)).");
            await Task.Delay(100, ct);
        }
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].Started >= calls[0].Ended, "The follow-up ran next to the immediate revoke.");
        Assert.True(calls[1].Started >= committedBefore + Modgud.Api.Tests.Infrastructure.ModgudWebApplicationFactory.SafetyNetDelay,
            $"The follow-up ran at {calls[1].Started:O}, before it was due.");
        await using var query = GetTenantedSession();
        Assert.Empty(await query.QueryAsync<string>(
            "select id::text from wolverine_dead_letters where message_type like '%RevokeUserAccess%' and position(convert_to(?, 'UTF8') in body) > 0",
            ct, user.Id.ToString()));
    }

    [Fact]
    public async Task An_update_that_applies_an_outdated_change_request_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Kept", lastname: "Name", acronym: $"ku{Guid.NewGuid():N}"[..10], email: $"ku{Guid.NewGuid():N}"[..14] + "@test.com");
        var requestId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserChangeRequest
            {
                Id = requestId, UserId = user.Id, Type = ChangeRequestType.Profile,
                Status = ChangeRequestStatus.AdminApprovalPending,
                Payload = """{"Firstname":"Applied"}""", RequestedAt = updatedAt, UpdatedAt = updatedAt,
            });
            await seed.SaveChangesAsync(ct);
        }

        // The caller loaded an older state of the request than the one stored now.
        using var scope = Factory.Services.CreateScope();
        var result = await GetTenantedMessageBus(scope).InvokeAsync<ErrorOr<UserDto>>(new UpdateUserCommand(
            user.Id, new Optional<string>("Applied"), default, default, default, default,
            new ChangeRequestApproval(requestId, updatedAt.AddSeconds(-30), null)), ct);

        Assert.True(result.IsError);
        Assert.Equal("ChangeRequest.Changed", result.FirstError.Code);
        await using var query = GetTenantedSession();
        Assert.Equal("Kept", (await query.LoadAsync<ApplicationUser>(user.Id, ct))!.Firstname);
        Assert.Equal(ChangeRequestStatus.AdminApprovalPending, (await query.LoadAsync<UserChangeRequest>(requestId, ct))!.Status);
    }
}
