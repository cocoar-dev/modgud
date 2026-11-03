using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Provisioning;

/// <summary>
/// FD-01 / FD-02 — realm drafts under concurrency. A test-side Marten listener holds the
/// requests at a chosen save; it never replaces the endpoints, the applier or PostgreSQL.
/// FD-01: the version check compared ExpectedVersion with the loaded document only, so two
/// overlapping saves of version N both passed and the last one silently won.
/// FD-02: an edit saved while an apply ran was deleted with the applied draft, unapplied.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RealmDraftConcurrencyTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Drafts = "/api/admin/realm-config/drafts";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overlapping_updates_of_one_version_cannot_both_win()
    {
        var (id, version) = await CreateDraftAsync("Overlap draft");
        using var barrier = new Hold(session => session.PendingChanges.AllChangedFor<RealmDraft>().Any(d => d.Id == id), arrivals: 2);
        using var listening = Listen(barrier);

        var first = Client.PutAsJsonAsync($"{Drafts}/{id}", new { ExpectedVersion = version, Name = "Renamed by A" }, JsonOptions, Ct);
        var second = Client.PutAsJsonAsync($"{Drafts}/{id}", new { ExpectedVersion = version, Name = "Renamed by B" }, JsonOptions, Ct);
        var responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(2, barrier.Arrivals);
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
        var stored = JsonNode.Parse(await Client.GetStringAsync($"{Drafts}/{id}", Ct))!;
        Assert.Equal(version + 1, stored["Version"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_edit_while_the_draft_is_applied_is_refused_not_lost()
    {
        // A draft with a real live change, so the applier writes something.
        var stage = await Client.PutAsJsonAsync($"{Drafts}/active/entities/groups",
            new { Name = $"FD02 group {Guid.NewGuid():N}"[..20] }, JsonOptions, Ct);
        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        var draft = JsonNode.Parse(await stage.Content.ReadAsStringAsync(Ct))!;
        var id = draft["Id"]!.GetValue<Guid>();

        // Hold the applier's live write of the staged group (inside its apply transaction).
        using var barrier = new Hold(session =>
            session.PendingChanges.AllChangedFor<Modgud.Authorization.Principals.Group>().Any() ||
            session.PendingChanges.Streams().SelectMany(s => s.Events)
                .Any(e => e.Data.GetType().Name.StartsWith("Group", StringComparison.Ordinal)), arrivals: 1);
        using var listening = Listen(barrier);

        var apply = Client.PostAsJsonAsync($"{Drafts}/{id}/apply", new { }, JsonOptions, Ct);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        // While the apply runs, another tab renames the same draft from its current version.
        var current = JsonNode.Parse(await Client.GetStringAsync($"{Drafts}/{id}", Ct))!;
        using var edit = await Client.PutAsJsonAsync($"{Drafts}/{id}",
            new { ExpectedVersion = current["Version"]!.GetValue<int>(), Name = "Edited during apply" }, JsonOptions, Ct);
        barrier.Release();
        using var applied = await apply.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Contains("Draft.Applying", await edit.Content.ReadAsStringAsync(Ct));
    }

    private async Task<(Guid Id, int Version)> CreateDraftAsync(string name)
    {
        var created = await Client.PostAsJsonAsync(Drafts, new { Name = name, Source = "empty" }, JsonOptions, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var node = JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!;
        return (node["Id"]!.GetValue<Guid>(), node["Version"]!.GetValue<int>());
    }

    private IDisposable Listen(Hold hold)
    {
        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        options.Listeners.Add(hold);
        return new Unlisten(() => { hold.Release(); options.Listeners.Remove(hold); });
    }

    private sealed class Unlisten(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>Holds matching saves until <paramref name="arrivals"/> have arrived (or
    /// until released).</summary>
    private sealed class Hold(Func<IDocumentSession, bool> matches, int arrivals) : DocumentSessionListenerBase, IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);
        public Task Reached => _reached.Task;

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (_open.Task.IsCompleted || !matches(session)) return;
            var n = Interlocked.Increment(ref _arrivals);
            if (n > arrivals) return;
            if (n == arrivals) _reached.TrySetResult();
            if (arrivals > 1 && n == arrivals) _open.TrySetResult();
            await _open.Task.WaitAsync(_timeout.Token);
        }

        public void Release() => _open.TrySetResult();
        public void Dispose() => _timeout.Dispose();
    }
}
