using System.Net;
using System.Net.Http.Json;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Pins the one-time-use guarantee for the credentials that previously only
/// *claimed* it in a comment. Two gaps were closed:
///
/// <list type="bullet">
///   <item><b>Cross-channel replay</b> — the web magic-link flow marks a
///   challenge consumed (it cannot Delete: Marten does not version-check
///   deletes, and a version-checked Store is what wins the race). The native
///   <c>urn:cocoar:magic</c> grant queried by user+hash and never looked at
///   <c>IsConsumed</c>, so a link already used in the browser stayed redeemable
///   through the native channel.</item>
///   <item><b>Concurrent redemption</b> — <c>EmailOtpChallenge</c> and
///   <c>PasskeyCeremony</c> consumed via Delete, which Marten does NOT
///   version-check, so two simultaneous redemptions of one code/ceremony could
///   both succeed. Both now carry a <c>ConsumedAt</c> marker written through a
///   version-checked Store.</item>
/// </list>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OneTimeCredentialAtomicityTests : IntegrationTestBase
{
    public OneTimeCredentialAtomicityTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ── concurrent redemption: exactly one racer may win ─────────────────────

    [Fact]
    public async Task PasskeyCeremony_ConcurrentConsume_OnlyOneWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();

        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new PasskeyCeremony
            {
                Id = id,
                OptionsJson = "{}",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PasskeyCeremony.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var sA = GetTenantedDocumentSession();
        await using var sB = GetTenantedDocumentSession();
        var a = await sA.LoadAsync<PasskeyCeremony>(id, ct);
        var b = await sB.LoadAsync<PasskeyCeremony>(id, ct);
        Assert.NotNull(a);
        Assert.NotNull(b);
        // Both racers observed a live, unconsumed ceremony — the exact window the
        // old Delete-based consume left open.
        Assert.False(a!.IsConsumed);
        Assert.False(b!.IsConsumed);

        a.ConsumedAt = DateTimeOffset.UtcNow;
        sA.Store(a);
        await sA.SaveChangesAsync(ct);

        b.ConsumedAt = DateTimeOffset.UtcNow;
        sB.Store(b);
        await Assert.ThrowsAsync<ConcurrencyException>(async () => await sB.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task EmailOtpChallenge_ConcurrentConsume_OnlyOneWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();

        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new EmailOtpChallenge
            {
                Id = userId,
                CodeHash = "hash",
                Attempts = 0,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(EmailOtpChallenge.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
                Email = "otp-race@test.com",
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var sA = GetTenantedDocumentSession();
        await using var sB = GetTenantedDocumentSession();
        var a = await sA.LoadAsync<EmailOtpChallenge>(userId, ct);
        var b = await sB.LoadAsync<EmailOtpChallenge>(userId, ct);
        Assert.NotNull(a);
        Assert.NotNull(b);

        a!.ConsumedAt = DateTimeOffset.UtcNow;
        sA.Store(a);
        await sA.SaveChangesAsync(ct);

        b!.ConsumedAt = DateTimeOffset.UtcNow;
        sB.Store(b);
        await Assert.ThrowsAsync<ConcurrencyException>(async () => await sB.SaveChangesAsync(ct));
    }

    // ── web passkey ceremony state must not be client-held ───────────────────

    [Fact]
    public async Task WebPasskeyLoginOptions_KeepsCeremonyServerSide_AndIgnoresForgedCookieState()
    {
        // The web login ceremony used to ship the FULL AssertionOptions to the
        // browser as plain Base64 (no signature, no encryption) and parse it back
        // as trusted input — so a client could rewrite the ceremony or replay an
        // old challenge. The cookie must now carry nothing but an opaque id, with
        // the authoritative state server-side, exactly like the native flow.
        var ct = TestContext.Current.CancellationToken;
        var client = Factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/account/passkey/login-options", new { }, ct);
        Assert.True(resp.IsSuccessStatusCode,
            $"login-options failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");

        var setCookie = Assert.Single(
            resp.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith(CookieName, StringComparison.Ordinal)));
        var cookieValue = setCookie[(CookieName.Length + 1)..].Split(';')[0];

        // Opaque id, not ceremony state.
        Assert.True(Guid.TryParse(cookieValue, out var ceremonyId),
            $"cookie must carry only a ceremony id, got: {cookieValue[..Math.Min(40, cookieValue.Length)]}");

        // The real options live server-side and the ceremony is still redeemable.
        await using (var read = GetTenantedDocumentSession())
        {
            var ceremony = await read.LoadAsync<PasskeyCeremony>(ceremonyId, ct);
            Assert.NotNull(ceremony);
            Assert.False(ceremony!.IsConsumed);
            Assert.Contains("challenge", ceremony.OptionsJson, StringComparison.OrdinalIgnoreCase);
        }

        // A cookie in the OLD client-held format (Base64 of attacker-chosen
        // options) must not authenticate anything — it isn't a ceremony id, so it
        // never resolves to server state.
        var forged = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("""{"challenge":"AAAA","rpId":"localhost"}"""));
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/passkey/login")
        {
            Content = JsonContent.Create(new { id = "x", response = new { } }),
        };
        req.Headers.Add("Cookie", $"{CookieName}={forged}");
        var forgedResp = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedResp.StatusCode);
    }

    private const string CookieName = "Modgud.Passkey.Challenge";

    // ── the consumed marker must actually gate redemption ────────────────────

    [Fact]
    public async Task EmailOtp_ConsumedCode_IsRejected_AndReissueStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Otp", lastname: "Reissue", acronym: "or",
            email: "otp-reissue@test.com", password: "TestPass1234");

        using var scope = Factory.Services.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();

        // Seed an already-consumed challenge with a known code. The hash matches
        // the service's own scheme (unsalted SHA-256 hex over the raw code), so the
        // rejection below can only come from the IsConsumed gate, not a hash miss.
        const string code = "123456";
        var codeHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

        await using (var seed = GetTenantedDocumentSession())
        {
            // RequestOtpAsync requires the user's explicit email-OTP opt-in.
            var u = await seed.LoadAsync<ApplicationUser>(user.Id, ct);
            u!.EmailOtpEnabled = true;
            seed.Store(u);
            seed.Store(new EmailOtpChallenge
            {
                Id = user.Id,
                CodeHash = codeHash,
                Attempts = 0,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(EmailOtpChallenge.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
                Email = "otp-reissue@test.com",
                ConsumedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync(ct);
        }

        // A consumed code must not authenticate, even though it is unexpired and
        // the hash matches.
        var replay = await otp.VerifyOtpAsync(user.Id, code, ct);
        Assert.True(replay.IsError);

        // Re-issuing must still work — two regressions are guarded here at once:
        // (1) the load-then-mutate rewrite (the document is version-checked now,
        // so storing a FRESH instance over the consumed row would be rejected),
        // and (2) the rate-limit exemption for consumed challenges (the row now
        // survives the consume, and without the exemption it would throttle the
        // very next request and lock the user out of email OTP).
        var reissue = await otp.RequestOtpAsync(user.Id, ct);
        Assert.False(reissue.IsError, reissue.IsError ? reissue.FirstError.Description : null);

        await using var read = GetTenantedDocumentSession();
        var fresh = await read.LoadAsync<EmailOtpChallenge>(user.Id, ct);
        Assert.NotNull(fresh);
        Assert.False(fresh!.IsConsumed);
        Assert.Equal(0, fresh.Attempts);
    }
}
