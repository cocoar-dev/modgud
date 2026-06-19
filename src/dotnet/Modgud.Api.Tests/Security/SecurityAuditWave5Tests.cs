using System.Security.Cryptography;
using System.Text;
using JasperFx;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Domain.OAuth.Consent;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 5 of the "similar bugs" remediation — TOCTOU / optimistic-concurrency
/// cluster (#22, #24, #25). Each finding is a read-modify-write that two
/// concurrent requests both win under the default last-writer-wins semantics.
///
/// The tests follow the wave's deterministic-guard strategy (handoff): rather than
/// firing real parallel requests (flaky → "all green" hides the race), they load
/// the SAME pre-state into two independent sessions and prove the second writer is
/// rejected / counted. That is exactly the race window, made repeatable.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave5Tests : IntegrationTestBase
{
    public SecurityAuditWave5Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #22 — refresh-token redeem is a guarded read-modify-write now. Two concurrent
    // /connect/token refreshes both load the same Valid token; the first redeem flips
    // it to Redeemed, and the second's stale Valid→Redeemed write must be REJECTED
    // (OpenIddict ConcurrencyException) instead of silently replaying past reuse
    // detection. RED before Wave 5: no optimistic concurrency → the second write
    // succeeds (last-writer-wins) and no exception is thrown.
    [Fact]
    public async Task RefreshTokenRedeem_ConcurrentUpdate_SecondWriterRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IOpenIddictTokenStore<OpenIddictTokenDocument>>();

        var token = new OpenIddictTokenDocument
        {
            Id = Guid.NewGuid().ToString(),
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = Guid.NewGuid().ToString(),
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            ReferenceId = Guid.NewGuid().ToString(),
            CreationDate = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(token, ct);

        // Two racers both load the live token (same row version V).
        var a = await store.FindByIdAsync(token.Id, ct);
        var b = await store.FindByIdAsync(token.Id, ct);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // First redeem wins (V → V').
        await store.SetStatusAsync(a!, OpenIddictConstants.Statuses.Redeemed, ct);
        await store.UpdateAsync(a!, ct);

        // Second redeem carries the stale version V → must lose, not replay.
        await store.SetStatusAsync(b!, OpenIddictConstants.Statuses.Redeemed, ct);
        await Assert.ThrowsAsync<OpenIddictExceptions.ConcurrencyException>(
            async () => await store.UpdateAsync(b!, ct));
    }

    // #22 (companion) — a NON-racing update path must still work. Guards against the
    // optimistic-concurrency change breaking ordinary token issuance/rotation.
    [Fact]
    public async Task TokenUpdate_SequentialUpdates_Succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IOpenIddictTokenStore<OpenIddictTokenDocument>>();

        var token = new OpenIddictTokenDocument
        {
            Id = Guid.NewGuid().ToString(),
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = Guid.NewGuid().ToString(),
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            CreationDate = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(token, ct);

        // Re-load between each update so the in-memory version stays current.
        var loaded = await store.FindByIdAsync(token.Id, ct);
        await store.SetStatusAsync(loaded!, OpenIddictConstants.Statuses.Redeemed, ct);
        await store.UpdateAsync(loaded!, ct);

        loaded = await store.FindByIdAsync(token.Id, ct);
        await store.SetStatusAsync(loaded!, OpenIddictConstants.Statuses.Revoked, ct);
        await store.UpdateAsync(loaded!, ct);

        var final = await store.FindByIdAsync(token.Id, ct);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, final!.Status);
    }

    // #22 (companion) — the lifecycle revoke sweep (logout-all / deactivate) must NOT
    // throw under the new optimistic concurrency even when a token is concurrently
    // rotated; the store retries the sweep on conflict. Asserts the sweep revokes.
    [Fact]
    public async Task RevokeBySubject_RevokesAllSubjectTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IOpenIddictTokenStore<OpenIddictTokenDocument>>();

        var subject = Guid.NewGuid().ToString();
        for (var i = 0; i < 3; i++)
        {
            await store.CreateAsync(new OpenIddictTokenDocument
            {
                Id = Guid.NewGuid().ToString(),
                Status = OpenIddictConstants.Statuses.Valid,
                Subject = subject,
                Type = OpenIddictConstants.TokenTypeHints.AccessToken,
                CreationDate = DateTimeOffset.UtcNow,
            }, ct);
        }

        var revoked = await store.RevokeBySubjectAsync(subject, ct);
        Assert.Equal(3, revoked);

        await foreach (var t in store.FindBySubjectAsync(subject, ct))
            Assert.Equal(OpenIddictConstants.Statuses.Revoked, t.Status);
    }

    // #24 — the Email-OTP attempt counter is incremented atomically. Two wrong guesses
    // that both observed Attempts=0 must BOTH be counted (final == 2), so the
    // MaxAttempts lockout is reliable. A read-modify-Store increment would lose one
    // (final == 1, last writer wins), letting an attacker exceed MaxAttempts.
    [Fact]
    public async Task EmailOtp_ConcurrentWrongGuesses_BothCounted_NoLostUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new EmailOtpChallenge
            {
                Id = userId,
                CodeHash = "irrelevant",
                Attempts = 0,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                CreatedAt = DateTimeOffset.UtcNow,
                Email = "otp@test.com",
            });
            await seed.SaveChangesAsync(ct);
        }

        // Two sessions that both observed Attempts=0 issue the same atomic increment
        // the service uses on a wrong code.
        await using var sA = GetTenantedDocumentSession();
        await using var sB = GetTenantedDocumentSession();
        sA.Patch<EmailOtpChallenge>(userId).Increment(c => c.Attempts, 1);
        sB.Patch<EmailOtpChallenge>(userId).Increment(c => c.Attempts, 1);
        await sA.SaveChangesAsync(ct);
        await sB.SaveChangesAsync(ct);

        await using var read = GetTenantedDocumentSession();
        var final = await read.LoadAsync<EmailOtpChallenge>(userId, ct);
        Assert.Equal(2, final!.Attempts);
    }

    // #24 (functional) — the service caps wrong guesses at MaxAttempts and then locks
    // the challenge out. Proves VerifyOtpAsync is wired to the counter.
    [Fact]
    public async Task EmailOtp_Verify_LocksOutAfterMaxAttempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new EmailOtpChallenge
            {
                Id = userId,
                CodeHash = HashCode("123456"),
                Attempts = 0,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                CreatedAt = DateTimeOffset.UtcNow,
                Email = "otp@test.com",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var scope = Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();

        // MaxAttempts wrong guesses come back InvalidCode; the next one is locked out.
        var sawLockout = false;
        for (var i = 0; i < EmailOtpChallenge.MaxAttempts + 1; i++)
        {
            var result = await svc.VerifyOtpAsync(userId, "000000", ct);
            Assert.True(result.IsError);
            if (result.FirstError.Code == "EmailOtp.TooManyAttempts")
            {
                sawLockout = true;
                break;
            }
            Assert.Equal("EmailOtp.InvalidCode", result.FirstError.Code);
        }
        Assert.True(sawLockout, "Email-OTP did not lock out after MaxAttempts wrong guesses.");
    }

    // #25 — the endpoint-level reuse contract (a consumed link → 401 on re-presentation)
    // is covered by MagicLinkTests.MagicLink_TokenIsOneTimeUse. This test covers the
    // concurrency leg: a version-checked consume so two parallel redemptions can't both win.
    //
    // magic-link "one-time use" is enforced by a version-checked consume. Two
    // concurrent redemptions both load the live challenge (via Query, mirroring the
    // endpoint); the first marks it ConsumedAt + Store and wins, and the second's
    // stale Store must be REJECTED (ConcurrencyException), which the login endpoint
    // maps to a 401 "already used". RED before Wave 5: no optimistic concurrency →
    // both stores succeed (last-writer-wins) and both redemptions proceed. NB: a
    // delete-based consume does NOT raise here — Marten only version-checks updates,
    // which is exactly why the endpoint marks-and-stores instead of deleting.
    [Fact]
    public async Task MagicLink_ConcurrentConsume_SecondWriterRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new MagicLinkChallenge
            {
                Id = id,
                UserId = userId,
                TokenHash = "hash",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var sA = GetTenantedDocumentSession();
        await using var sB = GetTenantedDocumentSession();
        var cA = await sA.Query<MagicLinkChallenge>().FirstOrDefaultAsync(c => c.Id == id, ct);
        var cB = await sB.Query<MagicLinkChallenge>().FirstOrDefaultAsync(c => c.Id == id, ct);
        Assert.NotNull(cA);
        Assert.NotNull(cB);

        cA!.ConsumedAt = DateTimeOffset.UtcNow;
        sA.Store(cA);
        await sA.SaveChangesAsync(ct);

        cB!.ConsumedAt = DateTimeOffset.UtcNow;
        sB.Store(cB);
        await Assert.ThrowsAsync<ConcurrencyException>(async () => await sB.SaveChangesAsync(ct));
    }

    // #26 — the consent-ticket consume is now a hard one-time-use guard. Two parallel
    // consent POSTs both load ConsumedAt==null; the first to claim (version-checked
    // Store) wins and the second's stale Store is REJECTED (ConcurrencyException →
    // 409), so it never proceeds to mint a duplicate authorization. RED before this
    // fix: no optimistic concurrency → both claims commit (last-writer-wins).
    [Fact]
    public async Task ConsentTicket_ConcurrentConsume_SecondWriterRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new ConsentTicket
            {
                Id = id,
                Subject = Guid.NewGuid(),
                ClientId = "client-26",
                RequestedScopes = ["openid"],
                AuthorizeRequestQuery = "?x=1",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var sA = GetTenantedDocumentSession();
        await using var sB = GetTenantedDocumentSession();
        var a = await sA.LoadAsync<ConsentTicket>(id, ct);
        var b = await sB.LoadAsync<ConsentTicket>(id, ct);
        Assert.NotNull(a);
        Assert.NotNull(b);

        a!.ConsumedAt = DateTimeOffset.UtcNow;
        sA.Store(a);
        await sA.SaveChangesAsync(ct);

        b!.ConsumedAt = DateTimeOffset.UtcNow;
        sB.Store(b);
        await Assert.ThrowsAsync<ConcurrencyException>(async () => await sB.SaveChangesAsync(ct));
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexStringLower(bytes);
    }
}
