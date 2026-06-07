using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 3 finding (med, security): the magic-link request endpoint must not let
/// an attacker enumerate registered emails. The response body was already
/// identical for known vs unknown addresses, but the success path skipped the
/// anti-timing jitter every failure branch applied — leaking which emails exist
/// via response time. These tests pin the content/behaviour contract and guard
/// that the success path now carries the jitter too.
///
/// <para>Each test uses its own isolated host so the per-IP rate limiter starts
/// fresh.</para>
/// </summary>
public class MagicLinkRequestTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Magic_link_request_is_content_identical_for_known_and_unknown_emails()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var user = await host.Factory.CreateTestUserWithIdentityAsync(
            "Magic", "User", "mluser", "mluser@cli.local", "TestPass1234", isRealmAdmin: false);

        var client = host.Factory.CreateClient();

        var known = await client.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "mluser@cli.local" }, ct);
        var unknown = await client.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "ghost@nowhere.local" }, ct);

        // Identical status + body — no content-level enumeration signal.
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));

        // Behavioural: a challenge is created only for the registered address.
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("system");
        var challenges = await session.Query<MagicLinkChallenge>().ToListAsync(ct);
        Assert.Single(challenges);
        Assert.Equal(user.Id, challenges[0].UserId);
    }

    [Fact]
    public async Task Magic_link_request_applies_the_anti_timing_jitter_on_the_success_path()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;
        var client = host.Factory.CreateClient();

        // Warm up the path (LINQ compilation / JIT) with a throwaway user so the
        // measured request reflects steady-state work, not first-call compilation.
        await host.Factory.CreateTestUserWithIdentityAsync(
            "Magic", "Warm", "mlwarm", "mlwarm@cli.local", "TestPass1234", isRealmAdmin: false);
        await client.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "mlwarm@cli.local" }, ct);

        // Measure a fresh success request. With the fix the success path applies
        // the same 100-300ms jitter every other branch had; without it, the
        // success path returns in a few milliseconds.
        await host.Factory.CreateTestUserWithIdentityAsync(
            "Magic", "Timed", "mltimed", "mltimed@cli.local", "TestPass1234", isRealmAdmin: false);

        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "mltimed@cli.local" }, ct);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 90,
            $"success path returned in {sw.ElapsedMilliseconds}ms — the anti-timing jitter was not applied to the success branch");
    }
}
