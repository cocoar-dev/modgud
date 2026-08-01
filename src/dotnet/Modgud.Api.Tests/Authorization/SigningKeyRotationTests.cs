using System.Net;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Realms;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Signing-key rotation overlap + janitor + admin-trigger.
///
/// <para>The store-level tests construct a fresh <see cref="RealmKeyStore"/>
/// with a controllable clock so they own both the DB state (each test runs on
/// the reset "system" tenant DB) AND time — bypassing the app's singleton
/// in-memory cache. This is what lets the overlap-expiry + janitor cutoff be
/// asserted deterministically without a 30-day wait. The downstream consumers
/// (<c>RealmTokenValidationHandler</c>, <c>RealmJwksHandler</c>) are unchanged
/// and simply iterate <c>GetVerificationKeysAsync</c>, so proving that method
/// returns the right set is the load-bearing assertion.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SigningKeyRotationTests : IntegrationTestBase
{
    public SigningKeyRotationTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Realm = "system";
    private const string Password = "TestPass1234";

    /// <summary>Minimal mutable clock — advance time without a 30-day wait or an extra package.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private RealmKeyStore NewStore(TimeProvider clock) =>
        new(
            Factory.Services.GetRequiredService<IDocumentStore>(),
            clock,
            Factory.Services.GetRequiredService<ISecurityAuditLog>());

    private static List<string?> Kids(IReadOnlyList<SecurityKey> keys) =>
        keys.Select(k => k.KeyId).ToList();

    // ── Verification set ────────────────────────────────────────────────

    [Fact]
    public async Task GetVerificationKeys_FreshRealm_ReturnsOnlyActiveKey()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));

        var active = await sut.GetActiveSigningCredentialsAsync(Realm);
        var keys = await sut.GetVerificationKeysAsync(Realm);

        Assert.Single(keys);
        Assert.Equal(active.Key.KeyId, keys[0].KeyId);
    }

    [Fact]
    public async Task Rotate_KeepsPreviousKeyInVerificationSet_DuringOverlap()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));

        var oldKid = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        var newKid = (await sut.RotateAsync(Realm)).Key.KeyId;

        Assert.NotEqual(oldKid, newKid);

        var kids = Kids(await sut.GetVerificationKeysAsync(Realm));
        // The new active validates new tokens AND the just-retired key still
        // validates tokens issued moments before the rotation — this is the
        // exact regression the overlap closes (userinfo/introspect/external-RS).
        Assert.Contains(newKid, kids);
        Assert.Contains(oldKid, kids);
        Assert.Equal(2, kids.Count);
    }

    [Fact]
    public async Task Rotate_AfterOverlapExpires_DropsPreviousKey()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var sut = NewStore(clock);

        var oldKid = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        var newKid = (await sut.RotateAsync(Realm)).Key.KeyId;

        // still inside the overlap window — both trusted
        Assert.Contains(oldKid, Kids(await sut.GetVerificationKeysAsync(Realm)));

        // step past the 30-day overlap; the cached set's ValidUntil lapses and
        // the rebuild drops the retired key without a janitor run.
        clock.Advance(RealmKeyStore.RotationOverlap + TimeSpan.FromDays(1));

        var kids = Kids(await sut.GetVerificationKeysAsync(Realm));
        Assert.Contains(newKid, kids);
        Assert.DoesNotContain(oldKid, kids);
        Assert.Single(kids);
    }

    [Fact]
    public async Task Rotate_MultipleTimes_StacksAllInOverlapKeys()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));

        var k0 = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        var k1 = (await sut.RotateAsync(Realm)).Key.KeyId;
        var k2 = (await sut.RotateAsync(Realm)).Key.KeyId;

        // Unlike the SAML 2-slot cert, realm keys are a collection: every
        // retired key inside its own overlap window stays verifiable.
        var kids = Kids(await sut.GetVerificationKeysAsync(Realm));
        Assert.Equal(new HashSet<string?> { k0, k1, k2 }, kids.ToHashSet());
    }

    // ── Janitor (PurgeExpiredRetiredKeysAsync) ──────────────────────────

    [Fact]
    public async Task Purge_RemovesExpiredRetiredKey_KeepsActive()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var sut = NewStore(clock);

        var oldKid = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        var newKid = (await sut.RotateAsync(Realm)).Key.KeyId;

        clock.Advance(RealmKeyStore.RotationOverlap + TimeSpan.FromDays(1));

        var purged = await sut.PurgeExpiredRetiredKeysAsync(Realm);
        Assert.Equal(1, purged);

        await using var session = GetTenantedSession(Realm);
        var remaining = await session.Query<RealmSigningKey>()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(remaining, k => k.KeyId == oldKid);
        Assert.Contains(remaining, k => k.KeyId == newKid && k.IsActive);

        // and the verification set rebuilt without the purged key
        Assert.DoesNotContain(oldKid, Kids(await sut.GetVerificationKeysAsync(Realm)));
    }

    [Fact]
    public async Task Purge_WithinOverlap_IsNoOp()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));

        await sut.GetActiveSigningCredentialsAsync(Realm);
        await sut.RotateAsync(Realm); // retired key created, still well inside overlap

        var purged = await sut.PurgeExpiredRetiredKeysAsync(Realm);

        Assert.Equal(0, purged);
        Assert.Equal(2, (await sut.GetVerificationKeysAsync(Realm)).Count);
    }

    // ── Admin rotate endpoint (authz + wiring) ──────────────────────────

    [Fact]
    public async Task RotateEndpoint_AsRealmAdmin_Returns200WithKid()
    {
        // Client is authenticated as the default realm-admin user.
        var resp = await Client.PostAsync(
            "/api/admin/realm-settings/rotate-signing-key",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        // Pin the wire contract: the API uses PascalCase (PropertyNamingPolicy=null).
        Assert.True(doc.RootElement.TryGetProperty("Kid", out var kid),
            "rotate response must carry a PascalCase 'Kid' property");
        Assert.False(string.IsNullOrWhiteSpace(kid.GetString()));
    }

    // ── Concurrency / out-of-band-rotation fixes ────────────────────────

    [Fact]
    public async Task Rotate_RetiresAllActiveKeys_SelfHealsMultiActive()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));
        await sut.GetActiveSigningCredentialsAsync(Realm); // bootstrap one active key

        // Inject a STRAY second active key directly (what a cross-process race
        // could leave behind without a DB unique constraint).
        await using (var write = GetTenantedDocumentSession(Realm))
        {
            var existing = await write.Query<RealmSigningKey>()
                .Where(k => k.IsActive).FirstAsync(TestContext.Current.CancellationToken);
            write.Store(new RealmSigningKey
            {
                Id = Guid.CreateVersion7(),
                RealmSlug = Realm,
                KeyId = existing.KeyId + "-dup",
                Algorithm = existing.Algorithm,
                PrivateKeyPem = existing.PrivateKeyPem,
                PublicKeyPem = existing.PublicKeyPem,
                IsActive = true,
                CreatedAt = existing.CreatedAt,
            });
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await sut.RotateAsync(Realm);

        // Rotation must retire EVERY prior active key → exactly one active remains.
        await using var read = GetTenantedSession(Realm);
        var actives = await read.Query<RealmSigningKey>()
            .Where(k => k.IsActive).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(actives);
    }

    [Fact]
    public async Task GetActive_PicksUpOutOfBandRotation_AfterRevalidateInterval()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        // Two store instances sharing the same DB but with separate in-memory
        // caches — i.e. two API instances / the separate CLI process.
        var instanceA = NewStore(clock);
        var instanceB = NewStore(clock);

        var kidA = (await instanceA.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId; // A caches K1

        var kidB = (await instanceB.RotateAsync(Realm)).Key.KeyId; // B rotates → DB now K2 active
        Assert.NotEqual(kidA, kidB);

        // Within the freshness window A still signs with its cached K1 (bounded staleness).
        Assert.Equal(kidA, (await instanceA.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId);

        // Past the revalidate interval A reconciles against the DB and adopts K2 —
        // so it stops signing with a key the janitor will eventually hard-delete.
        clock.Advance(RealmKeyStore.CacheRevalidateInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(kidB, (await instanceA.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId);
    }

    [Fact]
    public async Task GetVerificationKeys_PicksUpOutOfBandRotation_NeverOmitsNewActive()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var instanceA = NewStore(clock);
        var instanceB = NewStore(clock);

        var kidA = (await instanceA.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        // A warms its verification cache with the pre-rotation key set.
        Assert.Contains(kidA, Kids(await instanceA.GetVerificationKeysAsync(Realm)));

        var kidB = (await instanceB.RotateAsync(Realm)).Key.KeyId; // out-of-band rotation

        // After the freshness window, A's verification set MUST include the new
        // active key (kidB). Omitting it is exactly the TOCTOU/staleness defect
        // the rewrite + generation guard close — every freshly-signed token would
        // otherwise fail validation for the whole overlap window.
        clock.Advance(RealmKeyStore.CacheRevalidateInterval + TimeSpan.FromSeconds(1));
        var kids = Kids(await instanceA.GetVerificationKeysAsync(Realm));
        Assert.Contains(kidB, kids); // new active present
        Assert.Contains(kidA, kids); // old key still in its overlap window
    }

    [Fact]
    public async Task RotateEndpoint_WithoutPermission_Returns403()
    {
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "No", lastname: "Admin", acronym: "na",
            email: "na@test.com", password: Password, isRealmAdmin: false);
        var client = await CreateAuthenticatedClientAsync("na", Password);

        var resp = await client.PostAsync(
            "/api/admin/realm-settings/rotate-signing-key",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
