using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// FK-03 — a planned rotation must not sign with a key nobody has seen yet: peers re-read the
/// active key only every 60 s, external resource servers refresh their JWKS cache on their own
/// schedule. The new key is published first (JWKS + verification set) and signs only after
/// <see cref="RealmKeyStore.PrePublicationDelay"/>.
/// </summary>
public partial class SigningKeyRotationTests
{
    [Fact]
    public async Task RotateEndpoint_PublishesTheNewKeyBeforeItSigns()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Factory.Services.GetRequiredService<IRealmKeyStore>();
        var before = (await store.GetActiveSigningCredentialsAsync(Realm, ct)).Key.KeyId;

        var resp = await Client.PostAsync("/api/admin/realm-settings/rotate-signing-key", content: null, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var kid = doc.RootElement.GetProperty("Kid").GetString();

        Assert.NotEqual(before, kid);
        Assert.Equal(before, (await store.GetActiveSigningCredentialsAsync(Realm, ct)).Key.KeyId);
        Assert.Contains(kid, Kids(await store.GetVerificationKeysAsync(Realm, ct)));
    }

    [Fact]
    public async Task ScheduledRotation_TakesOverAfterThePrePublicationDelay()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var sut = NewStore(clock);
        var oldKid = (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId;

        var scheduled = await sut.ScheduleRotationAsync(Realm);
        Assert.NotEqual(oldKid, scheduled.KeyId);
        Assert.Equal(clock.GetUtcNow() + RealmKeyStore.PrePublicationDelay, scheduled.ActivatesAt);

        // Published, not yet signing.
        Assert.Equal(oldKid, (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId);
        Assert.Contains(scheduled.KeyId, Kids(await sut.GetVerificationKeysAsync(Realm)));

        // Asking again while one is pending returns that one, it does not stack keys.
        Assert.Equal(scheduled.KeyId, (await sut.ScheduleRotationAsync(Realm)).KeyId);

        clock.Advance(RealmKeyStore.PrePublicationDelay + RealmKeyStore.CacheRevalidateInterval);

        Assert.Equal(scheduled.KeyId, (await sut.GetActiveSigningCredentialsAsync(Realm)).Key.KeyId);
        var kids = Kids(await sut.GetVerificationKeysAsync(Realm));
        Assert.Contains(oldKid, kids);     // retired into the overlap, still verifies
        Assert.Contains(scheduled.KeyId, kids);
    }
}
