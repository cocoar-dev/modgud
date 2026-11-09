using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B7-SC05 — deactivating or deleting a service account committed the change and revoked its
/// tokens afterwards. When that revoke failed, the request failed but the account stayed inactive
/// or deleted with live tokens, and a repeat did not revoke (the account was no longer active, or
/// gone). The revoke is now also a safety-net follow-up written with the change.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ServiceAccountRevokeFollowUpTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task A_failing_revoke_after_a_deactivation_is_completed_by_the_follow_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var (saShort, saId) = await CreateServiceAccountAsync("svc-fu-deact", ct);
        var tokenId = await SeedTokenAsync(saId.ToString(), ct);

        Factory.GrantRevokerFaults.FailSubjectTokenRevokes = 2; // the immediate revoke and the first follow-up attempt
        using var resp = await Client.PutAsJsonAsync($"/api/service-account/{saShort}", new { IsActive = false }, ct);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"got {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");

        await WaitForStatusAsync(tokenId, OpenIddictConstants.Statuses.Revoked, ct);
        Assert.Equal(0, Factory.GrantRevokerFaults.FailSubjectTokenRevokes);
    }

    [Fact]
    public async Task A_failing_revoke_after_a_deletion_is_completed_by_the_follow_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var (saShort, saId) = await CreateServiceAccountAsync("svc-fu-del", ct);
        var tokenId = await SeedTokenAsync(saId.ToString(), ct);

        Factory.GrantRevokerFaults.FailSubjectTokenRevokes = 2;
        using var resp = await Client.DeleteAsync($"/api/service-account/{saShort}", ct);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"got {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");

        await WaitForStatusAsync(tokenId, OpenIddictConstants.Statuses.Revoked, ct);
        Assert.Equal(0, Factory.GrantRevokerFaults.FailSubjectTokenRevokes);
    }

    /// <summary>The safety net runs after its delay; an account reactivated meanwhile keeps the
    /// tokens it was issued after the reactivation.</summary>
    [Fact]
    public async Task An_account_reactivated_before_the_follow_up_is_due_keeps_its_new_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (saShort, saId) = await CreateServiceAccountAsync("svc-fu-react", ct);
        using (var off = await Client.PutAsJsonAsync($"/api/service-account/{saShort}", new { IsActive = false }, ct))
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using (var on = await Client.PutAsJsonAsync($"/api/service-account/{saShort}", new { IsActive = true }, ct))
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var fresh = await SeedTokenAsync(saId.ToString(), ct);

        await Task.Delay(ModgudWebApplicationFactory.SafetyNetDelay + TimeSpan.FromSeconds(3), ct);
        await using var session = GetTenantedDocumentSession();
        Assert.Equal(OpenIddictConstants.Statuses.Valid, (await session.LoadAsync<OpenIddictTokenDocument>(fresh, ct))!.Status);
    }

    private async Task<(string Short, Guid Guid)> CreateServiceAccountAsync(string name, CancellationToken ct)
    {
        using var resp = await Client.PostAsJsonAsync("/api/service-account", new { AccountName = name, Purpose = "b7-sc05" }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        var shortId = dto.GetProperty("Id").GetString()!;
        return (shortId, new ShortGuid(shortId).Guid);
    }

    private async Task<string> SeedTokenAsync(string subject, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        await using var session = GetTenantedDocumentSession();
        session.Store(new OpenIddictTokenDocument
        {
            Id = id, Subject = subject, ApplicationId = "b7-sc05-client",
            Status = OpenIddictConstants.Statuses.Valid, Type = OpenIddictConstants.TokenTypeHints.AccessToken,
            CreationDate = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);
        return id;
    }

    private async Task WaitForStatusAsync(string tokenId, string status, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            await using var session = GetTenantedDocumentSession();
            var token = await session.LoadAsync<OpenIddictTokenDocument>(tokenId, ct);
            if (token?.Status == status) return;
            Assert.True(DateTime.UtcNow < deadline, $"token {tokenId} is '{token?.Status}', expected '{status}'");
            await Task.Delay(200, ct);
        }
    }
}
