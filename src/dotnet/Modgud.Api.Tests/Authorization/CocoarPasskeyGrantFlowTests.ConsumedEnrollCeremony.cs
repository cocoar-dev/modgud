using System.Net;
using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B8-P1 — a <see cref="PasskeyEnrollCeremony"/> is consumed with a version-checked
/// <c>ConsumedAt</c> marker by both the web register and the native enroll finish, and
/// the row stays until expiry. A consumed ceremony is refused, and two overlapping native
/// finishes of one ceremony store one credential only.
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    [Fact]
    public async Task NativeEnroll_refuses_a_consumed_enroll_ceremony()
    {
        var ct = TestContext.Current.CancellationToken;
        var (bearer, ceremonyId, options) = await NativeEnrollBeginAsync();
        using var _ = bearer;

        // Consumed exactly as a finish leaves it.
        await using (var session = GetTenantedDocumentSession())
        {
            var ceremony = await session.LoadAsync<PasskeyEnrollCeremony>(Guid.Parse(ceremonyId), ct);
            ceremony!.ConsumedAt = DateTimeOffset.UtcNow;
            session.Store(ceremony);
            await session.SaveChangesAsync(ct);
        }

        using var enrolling = NewEnrollingKey();
        using var enrollResp = await NativeEnrollAsync(bearer, ceremonyId, Attest(enrolling, options));

        Assert.Equal(HttpStatusCode.BadRequest, enrollResp.StatusCode);
        Assert.Null(await LoadCredentialAsync(enrolling.CredentialId));
    }

    [Fact]
    public async Task NativeEnroll_overlapping_finishes_of_one_ceremony_store_one_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var (bearer, ceremonyId, options) = await NativeEnrollBeginAsync();
        using var _ = bearer;
        // Two different keys: the credential-id index cannot decide, only the
        // single use of the ceremony can.
        using var first = NewEnrollingKey();
        using var second = NewEnrollingKey();
        var firstBody = Attest(first, options);
        var secondBody = Attest(second, options);

        var storeOptions = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        using var barrier = new EnrollConsumeBarrier(Guid.Parse(ceremonyId));
        Task<HttpResponseMessage>[] requests = [];
        storeOptions.Listeners.Add(barrier);
        try
        {
            requests = [NativeEnrollAsync(bearer, ceremonyId, firstBody), NativeEnrollAsync(bearer, ceremonyId, secondBody)];
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60), ct);
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest },
                responses.Select(r => r.StatusCode).Order());
            Assert.Equal(2, barrier.Arrivals);
        }
        finally
        {
            barrier.Cancel();
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Preserve the original failure; never print request data. */ }
            storeOptions.Listeners.Remove(barrier);
            foreach (var request in requests)
                if (request.IsCompletedSuccessfully) (await request).Dispose();
        }

        var stored = new[] { await LoadCredentialAsync(first.CredentialId), await LoadCredentialAsync(second.CredentialId) };
        Assert.Single(stored, c => c is not null);
        await using var read = GetTenantedSession();
        var consumed = await read.LoadAsync<PasskeyEnrollCeremony>(Guid.Parse(ceremonyId), ct);
        Assert.NotNull(consumed?.ConsumedAt);
    }

    [Fact]
    public async Task NativeEnroll_with_a_malformed_attestation_is_a_400_not_a_500()
    {
        var (bearer, ceremonyId, _) = await NativeEnrollBeginAsync();
        using var _bearer = bearer;

        using var response = await NativeEnrollAsync(bearer, ceremonyId,
            """{"id":"x","rawId":"x","type":"public-key","response":"not-an-object"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Native bootstrap login for <c>app-a</c> (RP <c>a.localhost</c>), then a real
    /// <c>enroll/begin</c>. Returns the Bearer client, the ceremony id and its options.</summary>
    private async Task<(HttpClient Bearer, string CeremonyId, JsonElement Options)> NativeEnrollBeginAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("app-a", rpId: "a.localhost");

        using var bootstrap = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(bootstrap.CredentialId, bootstrap.CosePublicKey(), bootstrap.UserHandle, rpId: "a.localhost");
        var (bootCeremony, bootChallenge, bootRpId) = await BeginAsync(clientId: "app-a");
        var tokenResp = await PostPasskeyAsync("app-a", bootCeremony,
            bootstrap.CreateAssertionJson(bootChallenge, bootRpId, $"https://{bootRpId}"));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
        Assert.True(tokenResp.IsSuccessStatusCode, $"bootstrap login failed ({(int)tokenResp.StatusCode})");
        var bearer = Factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization =
            new("Bearer", JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString());

        var beginResp = await bearer.PostAsync("/connect/passkey/enroll/begin", content: null, ct);
        Assert.True(beginResp.IsSuccessStatusCode, $"enroll/begin failed ({(int)beginResp.StatusCode})");
        using var beginJson = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync(ct));
        return (bearer,
            beginJson.RootElement.GetProperty("ceremonyId").GetString()!,
            beginJson.RootElement.GetProperty("options").Clone());
    }

    private SoftwareWebAuthnAuthenticator NewEnrollingKey()
        // The enroll ceremony's Fido2User.Id is UTF8(user.Id).
        => new(Encoding.UTF8.GetBytes(DefaultUser!.Id.ToString()));

    private static string Attest(SoftwareWebAuthnAuthenticator key, JsonElement options)
    {
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        return key.CreateAttestationJson(options.GetProperty("challenge").GetString()!, rpId, $"https://{rpId}");
    }

    private static Task<HttpResponseMessage> NativeEnrollAsync(HttpClient bearer, string ceremonyId, string attestation)
        => bearer.PostAsync("/connect/passkey/enroll",
            new StringContent($"{{\"ceremonyId\":\"{ceremonyId}\",\"attestation\":{attestation}}}",
                Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    /// <summary>Holds both finishes at the consume until each has loaded the live
    /// ceremony, so the version check — not request timing — decides the winner.</summary>
    private sealed class EnrollConsumeBarrier(Guid ceremonyId) : DocumentSessionListenerBase, IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        private readonly TaskCompletionSource _consumes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (!session.PendingChanges.AllChangedFor<PasskeyEnrollCeremony>()
                    .Any(c => c.Id == ceremonyId && c.ConsumedAt is not null))
                return;
            if (Interlocked.Increment(ref _arrivals) == 2) _consumes.TrySetResult();
            await _consumes.Task.WaitAsync(_timeout.Token);
        }

        public void Cancel() => _timeout.Cancel();
        public void Dispose() => _timeout.Dispose();
    }
}
