using System.Net;
using System.Net.Http.Json;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Pins the account-lockout counter as concurrency-safe (P0-4).
///
/// <para>
/// The counter used to be incremented in memory (<c>user.AccessFailedCount++</c>)
/// and written back by <c>EventSourcedUserStore.UpdateAsync</c> as part of a
/// whole-document Store — which ASP.NET Identity triggers after EVERY
/// <c>AccessFailedAsync</c>. Concurrent failed logins therefore all read the same
/// value N and all wrote N+1: a burst of parallel attempts registered as roughly
/// ONE failure, so the five-attempt threshold never tripped and the lockout was
/// bypassable simply by not sending the guesses sequentially.
/// </para>
///
/// <para>
/// The counter is now a server-side jsonb increment and the lockout fields are
/// written exclusively through the <c>IUserLockoutStore</c> methods, never as part
/// of a full-document Store. These tests drive the real HTTP endpoint so each
/// attempt gets its own DI scope and therefore its own Marten session — the only
/// arrangement in which the original race can actually occur.
/// </para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class LockoutConcurrencyTests : IntegrationTestBase
{
    public LockoutConcurrencyTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Password = "TestPass1234";

    [Fact]
    public async Task Parallel_failed_logins_all_count_toward_the_lockout_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Race", lastname: "Lockout", acronym: "rl",
            email: "rl@test.com", password: Password);

        // Well above the 5-attempt threshold, fired simultaneously. Under the old
        // read-then-write increment these collapsed into a handful of recorded
        // failures; every one of them must now land.
        const int parallelAttempts = 12;
        var anon = Factory.CreateClient();

        await Task.WhenAll(Enumerable.Range(0, parallelAttempts).Select(_ =>
            anon.PostAsJsonAsync("/api/account/login",
                new { UserName = "rl", Password = "Wrong123!@#", RememberMe = false }, ct)));

        // The decisive assertion: the CORRECT password is now refused from this
        // (untrusted) client, because the burst exhausted the user's untrusted
        // failure bucket (ADR 0020). This is the property an attacker defeats by
        // going parallel — a read-then-write counter would leave the bucket below
        // the threshold and this request would succeed.
        var withCorrectPassword = await anon.PostAsJsonAsync("/api/account/login",
            new { UserName = "rl", Password = Password, RememberMe = false }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, withCorrectPassword.StatusCode);

        // The per-user failure counter recorded every attempt — and, since ADR 0020,
        // no longer locks the account itself (LockoutEnd stays the admin's lock):
        // the owner's own trusted devices are unaffected by a stranger's burst.
        await using var qs = GetTenantedSession();
        var securityData = await qs.LoadAsync<UserSecurityData>(user.Id, ct);
        Assert.NotNull(securityData);
        Assert.Equal(parallelAttempts, securityData!.AccessFailedCount);
        Assert.Null(securityData.LockoutEnd);
    }

    [Fact]
    public async Task Parallel_failed_logins_are_each_recorded_in_the_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Count", lastname: "Race", acronym: "cr",
            email: "cr@test.com", password: Password);

        // Deliberately BELOW the threshold so the counter is observable: crossing it
        // makes Identity reset the count to 0 and stamp LockoutEnd instead.
        const int parallelAttempts = 4;
        var anon = Factory.CreateClient();

        await Task.WhenAll(Enumerable.Range(0, parallelAttempts).Select(_ =>
            anon.PostAsJsonAsync("/api/account/login",
                new { UserName = "cr", Password = "Wrong123!@#", RememberMe = false }, ct)));

        await using var qs = GetTenantedSession();
        var securityData = await qs.LoadAsync<UserSecurityData>(user.Id, ct);
        Assert.NotNull(securityData);

        // Every attempt landed. The old in-memory increment typically left this at 1.
        Assert.Equal(parallelAttempts, securityData!.AccessFailedCount);
        Assert.Null(securityData.LockoutEnd);
    }

    [Fact]
    public async Task A_successful_login_still_clears_the_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Clear", lastname: "Counter", acronym: "cc",
            email: "cc@test.com", password: Password);

        var anon = Factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            await anon.PostAsJsonAsync("/api/account/login",
                new { UserName = "cc", Password = "Wrong123!@#", RememberMe = false }, ct);
        }

        var ok = await anon.PostAsJsonAsync("/api/account/login",
            new { UserName = "cc", Password = Password, RememberMe = false }, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Now that the counter is DB-authoritative rather than a mirror written back
        // by UpdateAsync, the reset must still travel all the way to the document.
        await using var qs = GetTenantedSession();
        var securityData = await qs.LoadAsync<UserSecurityData>(user.Id, ct);
        Assert.NotNull(securityData);
        Assert.Equal(0, securityData!.AccessFailedCount);
    }
}
