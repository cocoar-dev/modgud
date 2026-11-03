using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.Positions;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// B3-P2 — at most one live grant per (position, user). The issue endpoint checked with
/// a query and then started an independent grant stream, the position create deduplicated
/// the raw id strings, and the manifest create not at all: overlapping issues or two
/// spellings of one user id (ShortGuid / Guid) left two live grants, and revoking one of
/// them left the user authorised.
/// </summary>
public partial class PositionGrantTests
{
    [Fact]
    public async Task Overlapping_issues_for_one_user_leave_one_live_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-overlap-issue", ct);
        var user = await CreateUserAsync("ovl", ct);
        var userGuid = new ShortGuid(user).Guid;

        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        using var barrier = new IssueBarrier(userGuid);
        Task<HttpResponseMessage>[] requests = [];
        options.Listeners.Add(barrier);
        try
        {
            requests =
            [
                Client.PostAsJsonAsync($"/api/position/{fn}/grants", new { UserId = user }, JsonOptions, ct),
                Client.PostAsJsonAsync($"/api/position/{fn}/grants", new { UserId = user }, JsonOptions, ct),
            ];
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60), ct);
            Assert.Equal(2, barrier.Arrivals);
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
        }
        finally
        {
            barrier.Cancel();
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* keep the original failure */ }
            options.Listeners.Remove(barrier);
            foreach (var request in requests)
                if (request.IsCompletedSuccessfully) (await request).Dispose();
        }
        Assert.Equal(1, await LiveGrantsAsync(fn, userGuid, ct));
    }

    [Fact]
    public async Task Create_with_two_spellings_of_one_user_stages_one_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var user = await CreateUserAsync("alias", ct);
        var userGuid = new ShortGuid(user).Guid;

        var resp = await Client.PostAsJsonAsync("/api/position",
            new { AccountName = "fn-alias-grants", GrantUserIds = new[] { user, userGuid.ToString() } }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        var fn = (await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id;

        Assert.Equal(1, await LiveGrantsAsync(fn, userGuid, ct));
    }

    [Fact]
    public async Task Manifest_create_with_two_references_to_one_user_stages_one_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var user = await CreateUserAsync("mfalias", ct);
        var userGuid = new ShortGuid(user).Guid;

        var apply = await Client.PostAsJsonAsync("/api/admin/realm-config/apply", new
        {
            Positions = new[]
            {
                new { AccountName = "fn-manifest-alias", Grants = new[] { new { Id = user }, new { Id = userGuid.ToString() } } },
            },
        }, JsonOptions, ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(ct));

        await using var session = GetTenantedSession();
        var position = await session.Query<Modgud.Authorization.Principals.PositionPrincipal>()
            .SingleAsync(p => p.AccountName == "fn-manifest-alias" && !p.IsDeleted, ct);
        Assert.Equal(1, await LiveGrantsAsync(new ShortGuid(position.Id).ToString(), userGuid, ct));
    }

    private async Task<int> LiveGrantsAsync(string positionId, Guid userId, CancellationToken ct)
    {
        var position = new ShortGuid(positionId).Guid;
        await using var session = GetTenantedSession();
        return await session.Query<PositionGrant>()
            .CountAsync(g => g.PositionPrincipalId == position && g.UserId == userId && g.Status != PositionGrantStatus.Revoked, ct);
    }

    /// <summary>Holds both issues at their grant write until both have passed the check.</summary>
    private sealed class IssueBarrier(Guid userId) : DocumentSessionListenerBase, IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (!session.PendingChanges.Streams().SelectMany(s => s.Events)
                    .Any(e => e.Data is PositionGrantIssued issued && issued.UserId == userId))
                return;
            if (Interlocked.Increment(ref _arrivals) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(_timeout.Token);
        }

        public void Cancel() => _timeout.Cancel();
        public void Dispose() => _timeout.Dispose();
    }
}
