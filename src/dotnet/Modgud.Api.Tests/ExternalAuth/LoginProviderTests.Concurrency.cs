using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;
using Wolverine;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// EP-04 — provider writes had no conflict protection: an update appends a full-state
/// event without an expected stream version, so a later update silently reset a
/// disjoint earlier one (trust/policy fields included); and create checked slug and
/// display name by query only, so two overlapping creates both passed. A test-side
/// Marten listener holds both writes at their save so they really overlap.
/// </summary>
public partial class LoginProviderTests
{
    [Fact]
    public async Task An_update_loaded_before_another_committed_cannot_reset_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await CreateDisabledOidcAsync($"Upd-{Guid.NewGuid():N}"[..14]);
        var renamed = $"Renamed-{Guid.NewGuid():N}"[..16];

        // A (trust flag) loads the provider and is held right at its save; B (rename)
        // runs to completion meanwhile; then A goes on with its full-state event.
        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        var hold = new HoldFirst(s => s.PendingChanges.Streams().Any(st => st.Events.Any(e => e.Data is LoginProviderUpdatedEvent)));
        options.Listeners.Add(hold);
        ErrorOr<LoginProvider> first, second;
        try
        {
            var a = Task.Run(() => UpdateAsync(provider.Id, trustForAuthorization: true));
            await hold.Held.WaitAsync(TimeSpan.FromSeconds(30), ct);
            second = await UpdateAsync(provider.Id, displayName: renamed);
            hold.Release();
            first = await a.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        finally
        {
            hold.Release();
            options.Listeners.Remove(hold);
        }

        Assert.False(second.IsError, second.IsError ? second.FirstError.Description : "");
        Assert.True(first.IsError, "the stale update must not overwrite the committed rename");
        Assert.Equal("LoginProvider.ConcurrentChange", first.FirstError.Code);
        await using var query = GetTenantedSession();
        var stored = await query.LoadAsync<LoginProvider>(provider.Id, ct);
        Assert.Equal(renamed, stored!.DisplayName);
    }

    [Fact]
    public async Task Overlapping_creates_with_one_slug_create_one_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = $"c{Guid.NewGuid():N}"[..12];

        var results = await HoldTogetherAsync(
            s => s.PendingChanges.Streams().Any(st => st.Events.Any(e => e.Data is LoginProviderAddedEvent)),
            () => CreateAsync(slug, $"Dup-A-{Guid.NewGuid():N}"[..14]),
            () => CreateAsync(slug, $"Dup-B-{Guid.NewGuid():N}"[..14]));

        Assert.Single(results, r => !r.IsError);
        Assert.Equal("LoginProvider.SlugTaken", Assert.Single(results, r => r.IsError).FirstError.Code);
        await using var query = GetTenantedSession();
        Assert.Equal(1, await query.Query<LoginProvider>().CountAsync(p => p.Slug == slug && !p.IsDeleted, ct));
    }

    private async Task<ErrorOr<LoginProvider>[]> HoldTogetherAsync(
        Func<IDocumentSession, bool> matches, Func<Task<ErrorOr<LoginProvider>>> first, Func<Task<ErrorOr<LoginProvider>>> second)
    {
        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        var hold = new Hold(matches, arrivals: 2);
        options.Listeners.Add(hold);
        try
        {
            var results = await Task.WhenAll(Task.Run(first), Task.Run(second)).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(2, hold.Arrivals);
            return results;
        }
        finally
        {
            hold.Release();
            options.Listeners.Remove(hold);
        }
    }

    private async Task<LoginProvider> CreateDisabledOidcAsync(string name)
    {
        var created = await CreateAsync($"u{Guid.NewGuid():N}"[..12], name);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return created.Value;
    }

    private async Task<ErrorOr<LoginProvider>> CreateAsync(string slug, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        return await GetTenantedMessageBus(scope).InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: displayName,
            Slug: slug,
            FlavorData: JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""")));
    }

    private async Task<ErrorOr<LoginProvider>> UpdateAsync(Guid id, bool? trustForAuthorization = null, string? displayName = null)
    {
        using var scope = Factory.Services.CreateScope();
        return await GetTenantedMessageBus(scope).InvokeAsync<ErrorOr<LoginProvider>>(new UpdateLoginProviderCommand(
            Id: id,
            DisplayName: displayName is null ? default(Modgud.Domain.Common.Optional<string>) : displayName,
            Description: default, ClientId: default, Scopes: default, UserUpdateScript: default,
            StoreRawClaims: default, RawClaimsRetentionDays: default, AutoCreateUsers: default,
            AllowLinking: default, TrustForEmailLink: default, AllowedEmailDomains: default,
            IconName: default, ButtonColorHex: default, FlavorData: default, Enabled: default,
            TrustForAuthorization: trustForAuthorization is { } trust ? trust : default(Modgud.Domain.Common.Optional<bool>)));
    }

    private sealed class Hold(Func<IDocumentSession, bool> matches, int arrivals) : DocumentSessionListenerBase
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (_open.Task.IsCompleted || !matches(session)) return;
            if (Interlocked.Increment(ref _arrivals) >= arrivals) _open.TrySetResult();
            await _open.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }

        public void Release() => _open.TrySetResult();
    }

    /// <summary>Holds the first matching save until released; later ones pass.</summary>
    private sealed class HoldFirst(Func<IDocumentSession, bool> matches) : DocumentSessionListenerBase
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;
        public Task Held => _held.Task;

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (!matches(session) || Interlocked.Increment(ref _seen) != 1) return;
            _held.TrySetResult();
            await _open.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }

        public void Release() => _open.TrySetResult();
    }
}
