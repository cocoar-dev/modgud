using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Gdpr;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// gscd — the permanent erasure deleted the user's Principal but left its id in the
/// MemberIds of every manual group (auth-test: "Administrators → &lt;unknown 6ed1a3a1&gt;").
/// The erasure now takes the user out of every group, and the consistency check can prune
/// such leftovers of earlier erasures.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class GdprErasureGroupMembershipTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_permanently_erased_user_is_taken_out_of_every_group()
    {
        var erased = await Factory.CreateTestUserWithIdentityAsync("Gone", "Member", "GM", "gone-member@test.com");
        var kept = await Factory.CreateTestUserWithIdentityAsync("Kept", "Member", "KM", "kept-member@test.com");
        var manual = await CreateGroupAsync("erase-manual", [erased.Id, kept.Id]);
        var second = await CreateGroupAsync("erase-second", [erased.Id]);

        using (var scope = Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IGdprService>()
                .PermanentlyEraseAsync(erased.Id, DefaultUser!.Id, "gscd", Ct);
            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        }

        await using var session = GetTenantedDocumentSession();
        Assert.Equal([kept.Id], (await session.LoadAsync<Group>(manual, Ct))!.MemberIds);
        Assert.Empty((await session.LoadAsync<Group>(second, Ct))!.MemberIds);
        Assert.Equal("OK", await DanglingMembersStatusAsync());
    }

    [Fact]
    public async Task Leftover_member_ids_of_earlier_erasures_can_be_pruned()
    {
        var live = await Factory.CreateTestUserWithIdentityAsync("Live", "Member", "LM", "live-member@test.com");
        var erasedEarlier = Guid.NewGuid(); // no Principal at all, as an erasure before the fix left it
        var group = await CreateGroupAsync("prune-manual", [live.Id, erasedEarlier]);
        Assert.Equal("ISSUES_FOUND", await DanglingMembersStatusAsync());

        var prune = await Client.PostAsync("/api/admin/projections/consistency-check/dangling-members/prune", null, Ct);

        Assert.True(prune.IsSuccessStatusCode, await prune.Content.ReadAsStringAsync(Ct));
        var body = await prune.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, body.GetProperty("Removed").GetInt32());
        await using var session = GetTenantedDocumentSession();
        Assert.Equal([live.Id], (await session.LoadAsync<Group>(group, Ct))!.MemberIds);
        Assert.Equal("OK", await DanglingMembersStatusAsync());
    }

    private async Task<Guid> CreateGroupAsync(string name, List<Guid> members)
    {
        var id = Guid.NewGuid();
        await using var session = GetTenantedDocumentSession();
        session.Events.StartStream<Group>(id, new GroupCreatedEvent(id, name, null, members, []));
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<string> DanglingMembersStatusAsync()
    {
        var response = await Client.GetAsync("/api/admin/projections/consistency-check", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("Checks").EnumerateArray()
            .Single(c => c.GetProperty("Id").GetString() == "dangling-members")
            .GetProperty("Status").GetString()!;
    }
}
