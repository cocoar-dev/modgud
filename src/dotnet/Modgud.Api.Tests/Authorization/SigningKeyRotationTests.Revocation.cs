using Marten;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// FK-01 — store level: a revoked key leaves the verification set (and so the JWKS) at once,
/// independent of its overlap window, and the janitor deletes it.
/// </summary>
public partial class SigningKeyRotationTests
{
    [Fact]
    public async Task Rotate_with_revocation_drops_the_previous_key_at_once()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));

        var oldKid = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        var newKid = (await sut.RotateAsync(Realm, revokePrevious: true)).Key.KeyId;

        var kids = Kids(await sut.GetVerificationKeysAsync(Realm));
        Assert.Equal([newKid], kids);
        Assert.DoesNotContain(oldKid, kids);
    }

    [Fact]
    public async Task Revoking_retired_keys_drops_them_and_keeps_the_active_key()
    {
        var sut = NewStore(new TestClock(DateTimeOffset.UtcNow));

        var k0 = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;
        var k1 = (await sut.RotateAsync(Realm)).Key.KeyId;
        var k2 = (await sut.RotateAsync(Realm)).Key.KeyId;

        Assert.Equal(2, await sut.RevokeRetiredKeysAsync(Realm));
        Assert.Equal([k2], Kids(await sut.GetVerificationKeysAsync(Realm)));
        Assert.Equal(0, await sut.RevokeRetiredKeysAsync(Realm)); // nothing left to revoke

        // The janitor deletes revoked keys right away, not after the overlap.
        Assert.Equal(2, await sut.PurgeExpiredRetiredKeysAsync(Realm));
        await using var session = GetTenantedSession(Realm);
        var remaining = await session.Query<RealmSigningKey>().ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(remaining, k => k.KeyId == k0 || k.KeyId == k1);
        Assert.Contains(remaining, k => k.KeyId == k2 && k.IsActive);
    }
}
