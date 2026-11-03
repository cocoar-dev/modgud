using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B5-01 = EG-09 — a magic link redeems at most once through the native grant, also for
/// two overlapping token requests. A test-side Marten listener holds both requests at the
/// consume (a Delete before the fix, a ConsumedAt store after it) until both have loaded
/// the same live challenge; it never replaces the grant handler or PostgreSQL.
/// </summary>
public partial class CocoarNativeGrantFlowTests
{
    [Fact]
    public async Task Magic_Grant_overlapping_redemptions_mint_tokens_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-magic-app");
        var (userId, token) = await SeedMagicLinkAsync(DefaultUser!.Id);

        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        using var barrier = new MagicConsumeBarrier();
        Task<HttpResponseMessage>[] requests = [];
        options.Listeners.Add(barrier);
        try
        {
            requests = [PostMagicAsync(userId, token), PostMagicAsync(userId, token)];
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60), ct);
            Assert.Equal(2, barrier.Arrivals);
            Assert.Single(responses, r => r.IsSuccessStatusCode);
            var refused = Assert.Single(responses, r => !r.IsSuccessStatusCode);
            Assert.Contains("invalid_grant", await refused.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            barrier.Cancel();
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Preserve the original failure; never print request data. */ }
            options.Listeners.Remove(barrier);
            foreach (var request in requests)
                if (request.IsCompletedSuccessfully) (await request).Dispose();
        }
    }

    /// <summary>Holds both redemptions at the consume of the magic-link challenge.</summary>
    private sealed class MagicConsumeBarrier : DocumentSessionListenerBase, IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        private readonly TaskCompletionSource _consumes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            var consumes = session.PendingChanges.DeletionsFor<MagicLinkChallenge>().Any()
                || session.PendingChanges.AllChangedFor<MagicLinkChallenge>().Any(c => c.ConsumedAt is not null);
            if (!consumes) return;
            if (Interlocked.Increment(ref _arrivals) == 2) _consumes.TrySetResult();
            await _consumes.Task.WaitAsync(_timeout.Token);
        }

        public void Cancel() => _timeout.Cancel();
        public void Dispose() => _timeout.Dispose();
    }
}
