using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Plan „Teilfehler nach dem Commit“, step 0 — the foundation for follow-up effects. A follow-up
/// published through <see cref="IFollowUpOutbox"/> is part of the realm session's commit: it is
/// handled exactly once, in the realm of that session, after the commit; it never exists when
/// the session is not committed; and a failing handler is retried.
/// </summary>
public class FollowUpOutboxTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    private const string Realm = "fu-acme";

    [Fact]
    public async Task A_committed_follow_up_is_handled_once_in_its_realm_and_a_rolled_back_one_never()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        await CreateRealmAsync(factory, ct);
        var recorder = factory.Services.GetRequiredService<FollowUpProbeRecorder>();

        var committed = Guid.NewGuid();
        var rolledBack = Guid.NewGuid();
        await PublishAsync(factory, Realm, new FollowUpProbe(committed), commit: true);
        await PublishAsync(factory, Realm, new FollowUpProbe(rolledBack), commit: false);

        await WaitUntilAsync(() => recorder.HandledIn(committed).Count > 0, ct);
        // Give a duplicate delivery or a late rolled-back message the chance to show up.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        Assert.Equal([Realm], recorder.HandledIn(committed));
        Assert.Equal(0, recorder.Attempts(rolledBack));
        // The envelope is in the realm database's durable inbox (where a crash recovery finds it),
        // marked handled; the master database's inbox never saw it.
        Assert.Equal(["Handled"], await EnvelopeStatusesAsync(factory.Services.GetRequiredService<IDocumentStore>().QuerySession(Realm), committed, ct));
        Assert.Empty(await EnvelopeStatusesAsync(factory.Services.GetRequiredService<IGlobalStore>().QuerySession(), committed, ct));
        Assert.Empty(await EnvelopeStatusesAsync(factory.Services.GetRequiredService<IDocumentStore>().QuerySession(Realm), rolledBack, ct));
    }

    [Fact]
    public async Task A_failing_follow_up_is_retried_until_it_succeeds()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        await CreateRealmAsync(factory, ct);
        var recorder = factory.Services.GetRequiredService<FollowUpProbeRecorder>();

        var id = Guid.NewGuid();
        await PublishAsync(factory, Realm, new FollowUpProbe(id, FailTimes: 2), commit: true);

        await WaitUntilAsync(() => recorder.HandledIn(id).Count > 0, ct, TimeSpan.FromSeconds(30));
        Assert.Equal(3, recorder.Attempts(id));
        Assert.Equal([Realm], recorder.HandledIn(id));
    }

    private static async Task CreateRealmAsync(ColdStartWebApplicationFactory factory, CancellationToken ct)
    {
        var created = await factory.Services.GetRequiredService<IRealmProvisioningService>().CreateRealmAsync(new CreateRealmDto
        {
            Slug = Realm,
            DisplayName = "Follow-up Acme",
            Domains = [$"{Realm}.localhost"],
            InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@fu-acme.test" },
        }, ct);
        Assert.False(created.IsError);
    }

    private static async Task PublishAsync(ColdStartWebApplicationFactory factory, string realm, FollowUpProbe probe, bool commit)
    {
        using var tenant = TenantContext.Enter(realm);
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await scope.ServiceProvider.GetRequiredService<IFollowUpOutbox>().PublishAsync(session, probe);
        if (commit) await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Statuses of the durable-inbox rows whose message carries <paramref name="id"/>
    /// (the row's own id is the envelope's, not the message's).</summary>
    private static async Task<IReadOnlyList<string>> EnvelopeStatusesAsync(IQuerySession session, Guid id, CancellationToken ct)
    {
        await using var _ = session;
        var schemas = await session.QueryAsync<string>(
            "select table_schema from information_schema.tables where table_name = 'wolverine_incoming_envelopes'", ct);
        var statuses = new List<string>();
        foreach (var schema in schemas)
        {
            statuses.AddRange(await session.QueryAsync<string>(
                $"select status from \"{schema}\".wolverine_incoming_envelopes where position(convert_to(?, 'UTF8') in body) > 0",
                ct, id.ToString()));
        }
        return statuses;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The follow-up was not handled in time.");
            await Task.Delay(100, ct);
        }
    }
}
