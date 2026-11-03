using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;

namespace Modgud.Api.Tests.Account;

/// <summary>
/// B8-P1 — a web passkey enrollment ceremony is redeemed at most once, also by two
/// overlapping finishes. A test-side Marten listener holds both requests at the
/// version-checked consume until each has loaded the same live ceremony; it never
/// replaces the HTTP endpoints, FIDO2 verification or PostgreSQL. Before the fix both
/// finishes deleted the ceremony and stored a credential — with the same key two
/// entries, of which deleting one left the key able to sign in.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PasskeyWebEnrollmentConcurrencyTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sequential_replay_with_original_cookie_is_refused_by_server_state()
    {
        using var key = NewKey();
        var started = await BeginAsync();
        using var first = FinishClient(started.Cookie);
        using var second = FinishClient(started.Cookie);
        var attestation = Attestation(key, started.Options);
        using var registered = await FinishAsync(first, attestation);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        using var replay = await FinishAsync(second, attestation);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Single(await CredentialsAsync());
        var consumed = await Factory.GetDocumentAsync<PasskeyEnrollCeremony>(started.Id);
        Assert.NotNull(consumed?.ConsumedAt);
    }

    [Fact]
    public async Task Overlapping_same_key_finishes_store_one_credential_and_its_deletion_revokes_the_key()
    {
        using var key = NewKey();
        await OverlapAsync(key, key);
        var credential = Assert.Single(await CredentialsAsync());
        Assert.True(credential.CredentialId.SequenceEqual(key.CredentialId));

        // A real signed web login proves the factor the deletion asks for.
        using var signedIn = await LoginAsync(key, 1, succeeds: true);
        using var removed = await signedIn.DeleteAsync($"/api/account/passkey/{credential.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(await CredentialsAsync());

        // No twin is left behind: the same key no longer signs in.
        using var refused = await LoginAsync(key, 2, succeeds: false);
    }

    [Fact]
    public async Task Overlapping_different_keys_cannot_both_redeem_the_same_web_ceremony()
    {
        using var first = NewKey();
        using var second = NewKey();
        await OverlapAsync(first, second);
        var credential = Assert.Single(await CredentialsAsync());
        Assert.True(credential.CredentialId.SequenceEqual(first.CredentialId)
            || credential.CredentialId.SequenceEqual(second.CredentialId));
    }

    [Fact]
    public async Task Credential_id_unique_index_rejects_a_second_document_with_the_same_id()
    {
        var credentialId = RandomBytes();
        await StoreCredentialAsync(credentialId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StoreCredentialAsync(credentialId));
        Assert.True(PasskeyCredentialUniqueness.IsDuplicateCredentialId(ex), ex.GetType().FullName);
        await StoreCredentialAsync(RandomBytes()); // a different id is still fine

        await using var session = GetTenantedSession();
        var definition = Assert.Single(await session.QueryAsync<string>(
            "select indexdef from pg_indexes where indexname = ?", PasskeyCredentialUniqueness.IndexName));
        TestContext.Current.TestOutputHelper?.WriteLine($"B8-P1 index: {definition}");
        Assert.StartsWith("CREATE UNIQUE INDEX", definition);
        Assert.DoesNotContain("jsonb", definition);
        Assert.Equal(2, (await session.Query<StoredPasskeyCredential>().ToListAsync(Ct)).Count);

        // Marten must see the live index as matching, or it would drop and recreate
        // it whenever the storage is ensured.
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        var database = await store.Storage.FindOrCreateDatabase("system");
        var feature = Assert.IsType<StoreOptions>(store.Options).Storage.FindFeature(typeof(StoredPasskeyCredential));
        var migration = await database.CreateMigrationAsync(feature, Ct);
        Assert.Equal(Weasel.Core.SchemaPatchDifference.None, migration.Difference);
    }

    [Fact]
    public async Task Overlapping_enrollments_of_one_key_in_two_ceremonies_store_it_once()
    {
        using var key = NewKey();
        var a = await BeginAsync();
        var b = await BeginAsync();
        Assert.NotEqual(a.Id, b.Id);
        using var tabA = FinishClient(a.Cookie);
        using var tabB = FinishClient(b.Cookie);
        var bodyA = Attestation(key, a.Options);
        var bodyB = Attestation(key, b.Options);
        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        using var barrier = new CredentialWriteBarrier(DefaultUser!.Id);
        Task<HttpResponseMessage>[] requests = [];
        options.Listeners.Add(barrier);
        try
        {
            requests = [FinishAsync(tabA, bodyA), FinishAsync(tabB, bodyB)];
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            // Both passed FIDO2 and the read-then-insert uniqueness check.
            Assert.Equal(2, barrier.Arrivals);
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest },
                responses.Select(r => r.StatusCode).Order());
            var refused = responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest);
            Assert.Contains("already registered", await refused.Content.ReadAsStringAsync(Ct));
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
        var credential = Assert.Single(await CredentialsAsync());
        Assert.True(credential.CredentialId.SequenceEqual(key.CredentialId));
    }

    private static byte[] RandomBytes() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private async Task StoreCredentialAsync(byte[] credentialId)
    {
        await using var session = GetTenantedDocumentSession();
        session.Store(new StoredPasskeyCredential
        {
            Id = Guid.CreateVersion7(),
            UserId = DefaultUser!.Id,
            CredentialId = credentialId,
            PublicKey = [9, 9],
            UserHandle = [8, 8],
            DisplayName = "Passkey",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
    }

    private SoftwareWebAuthnAuthenticator NewKey()
        // Web register-options uses the UTF8 Guid string for Fido2User.Id.
        => new(Encoding.UTF8.GetBytes(DefaultUser!.Id.ToString()));

    private async Task<(Guid Id, JsonElement Options, string Cookie)> BeginAsync()
    {
        using var cookies = new SnapshotCookies();
        using var browser = Factory.CreateDefaultClient(cookies);
        using var login = await browser.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = DefaultPassword }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var begin = await browser.PostAsync("/api/account/passkey/register-options", null, Ct);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var options = await begin.Content.ReadFromJsonAsync<JsonElement>(Ct);
        await using var session = GetTenantedSession();
        var ceremony = await session.Query<PasskeyEnrollCeremony>()
            .Where(c => c.UserId == DefaultUser!.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstAsync(Ct);
        Assert.Null(ceremony.ClientId);
        Assert.Equal("localhost", ceremony.RpId);
        return (ceremony.Id, options, cookies.ForFinish());
    }

    private static string Attestation(SoftwareWebAuthnAuthenticator key, JsonElement options)
        => key.CreateAttestationJson(options.GetProperty("challenge").GetString()!,
            options.GetProperty("rp").GetProperty("id").GetString()!, "https://localhost");

    private HttpClient FinishClient(string cookie)
        => Factory.CreateDefaultClient(new FixedCookies(cookie));

    private static Task<HttpResponseMessage> FinishAsync(HttpClient browser, string attestation)
        => browser.PostAsync("/api/account/passkey/register",
            new StringContent(attestation, Encoding.UTF8, "application/json"), Ct);

    private async Task OverlapAsync(SoftwareWebAuthnAuthenticator firstKey, SoftwareWebAuthnAuthenticator secondKey)
    {
        var started = await BeginAsync();
        using var first = FinishClient(started.Cookie);
        using var second = FinishClient(started.Cookie);
        // Create payloads before starting concurrent operations on either key.
        var firstBody = Attestation(firstKey, started.Options);
        var secondBody = ReferenceEquals(firstKey, secondKey) ? firstBody : Attestation(secondKey, started.Options);
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        var options = Assert.IsType<StoreOptions>(store.Options);
        using var overlap = new OverlapListener(started.Id, DefaultUser!.Id);
        Task<HttpResponseMessage>[] requests = [];
        // Test collection is serialized. Only the two requests below redeem this
        // user's ceremony; remove the observer after draining them, even on failure.
        options.Listeners.Add(overlap);
        try
        {
            requests = [FinishAsync(first, firstBody), FinishAsync(second, secondBody)];
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(2, overlap.Loaded);
            Assert.Equal(2, overlap.ConsumeArrivals);
            Assert.Equal(1, overlap.CredentialWrites);
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest },
                responses.Select(r => r.StatusCode).Order());
            var consumed = await Factory.GetDocumentAsync<PasskeyEnrollCeremony>(started.Id);
            Assert.NotNull(consumed?.ConsumedAt);
        }
        finally
        {
            overlap.Cancel();
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Preserve the original failure; never print request data. */ }
            options.Listeners.Remove(overlap);
            foreach (var request in requests)
                if (request.IsCompletedSuccessfully) request.Result.Dispose();
        }
    }

    private async Task<IReadOnlyList<StoredPasskeyCredential>> CredentialsAsync()
    {
        await using var session = GetTenantedSession();
        return await session.Query<StoredPasskeyCredential>()
            .Where(c => c.UserId == DefaultUser!.Id).ToListAsync(Ct);
    }

    private async Task<HttpClient> LoginAsync(SoftwareWebAuthnAuthenticator key, uint counter, bool succeeds)
    {
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        browser.BaseAddress = new Uri("https://localhost");
        try
        {
            using var begin = await browser.PostAsJsonAsync("/api/account/passkey/login-options",
                new { UserName = "tu" }, Ct);
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            var options = await begin.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("localhost", options.GetProperty("rpId").GetString());
            var assertion = key.CreateAssertionJson(options.GetProperty("challenge").GetString()!,
                "localhost", "https://localhost", counter);
            using var result = await browser.PostAsync("/api/account/passkey/login",
                new StringContent(assertion, Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, result.StatusCode);
            using var me = await browser.GetAsync("/api/account/me", Ct);
            Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, me.StatusCode);
            if (succeeds)
            {
                var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Assert.Equal(new ShortGuid(DefaultUser!.Id).ToString(), body.GetProperty("Id").GetString());
            }
            return browser;
        }
        catch
        {
            browser.Dispose();
            throw;
        }
    }

    private sealed class SnapshotCookies : DelegatingHandler
    {
        private readonly CookieContainer _cookies = new();
        public string ForFinish() => _cookies.GetCookieHeader(new Uri("http://localhost/api/account/passkey/register"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = _cookies.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) _cookies.SetCookies(request.RequestUri!, value);
            return response;
        }
    }

    private sealed class FixedCookies(string cookie) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Add("Cookie", cookie);
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>Holds both finishes at the consume until each has loaded the live
    /// ceremony, so the version check — not request timing — decides the winner.</summary>
    private sealed class OverlapListener(Guid ceremonyId, Guid userId) : DocumentSessionListenerBase, IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        private readonly TaskCompletionSource _consumes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loaded;
        private int _consumeArrivals;
        private int _credentialWrites;
        public int Loaded => Volatile.Read(ref _loaded);
        public int ConsumeArrivals => Volatile.Read(ref _consumeArrivals);
        public int CredentialWrites => Volatile.Read(ref _credentialWrites);

        public override void DocumentLoaded(object id, object document)
        {
            if (document is PasskeyEnrollCeremony { ConsumedAt: null } ceremony
                && ceremony.Id == ceremonyId && ceremony.UserId == userId)
                Interlocked.Increment(ref _loaded);
        }

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (session.PendingChanges.AllChangedFor<PasskeyEnrollCeremony>()
                .Any(c => c.Id == ceremonyId && c.ConsumedAt is not null))
            {
                if (Interlocked.Increment(ref _consumeArrivals) == 2) _consumes.TrySetResult();
                await _consumes.Task.WaitAsync(_timeout.Token);
            }
            if (session.PendingChanges.AllChangedFor<StoredPasskeyCredential>().Any(c => c.UserId == userId))
                Interlocked.Increment(ref _credentialWrites);
        }

        public void Cancel() => _timeout.Cancel();
        public void Dispose() => _timeout.Dispose();
    }

    /// <summary>Holds two enrollments of the user after FIDO2 and the uniqueness
    /// callback, just before the credential insert, so only the index decides.</summary>
    private sealed class CredentialWriteBarrier(Guid userId) : DocumentSessionListenerBase, IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        private readonly TaskCompletionSource _writes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (!session.PendingChanges.AllChangedFor<StoredPasskeyCredential>().Any(c => c.UserId == userId)) return;
            if (Interlocked.Increment(ref _arrivals) == 2) _writes.TrySetResult();
            await _writes.Task.WaitAsync(_timeout.Token);
        }

        public void Cancel() => _timeout.Cancel();
        public void Dispose() => _timeout.Dispose();
    }
}
