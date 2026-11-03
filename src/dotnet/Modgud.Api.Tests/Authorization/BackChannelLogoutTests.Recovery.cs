using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.BackChannelLogout;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// CL01 — the last attempt was claimed with <c>NextAttemptAt = MaxValue</c> before the
/// token was minted and posted; an attempt interrupted after that claim left a row no
/// sweep would ever pick up again: no final POST, no final status, no audit.
/// CL02 — the in-process dispatcher attempted a queued delivery without checking that it
/// was due, so a delivery the retry job had just attempted (and scheduled a minute out)
/// was sent again at once, spending the retry budget early.
/// </summary>
public partial class BackChannelLogoutTests
{
    [Fact]
    public async Task An_interrupted_last_attempt_is_picked_up_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var rp = await LogOutWithFailingRelyingPartyAsync("lastcut");

        // The delivery has used every scheduled retry; its last attempt is due now.
        await using (var mutate = GetTenantedDocumentSession())
        {
            var pending = await mutate.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).SingleAsync(ct);
            pending.Attempts = BackChannelLogoutConstants.RetrySchedule.Length;
            pending.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            mutate.Store(pending);
            await mutate.SaveChangesAsync(ct);
        }

        // The last attempt is interrupted mid-POST (request abort, shutdown).
        Sink.Hang(rp.LogoutUri!);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task sweep;
        using (var scope = CreateTenantScope())
        {
            sweep = scope.ServiceProvider.GetRequiredService<IBackChannelLogoutDeliverer>().SweepDueAsync(interrupt.Token);
            Assert.Equal(2, (await Sink.WaitForAsync(rp.LogoutUri!, 2)).Count);
            await interrupt.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
        }

        // The row stays recoverable: due again within a bounded lease, not never.
        await using (var query = GetTenantedSession())
        {
            var pending = Assert.Single(await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).ToListAsync(ct));
            Assert.True(pending.NextAttemptAt < DateTimeOffset.UtcNow.AddHours(1),
                $"the interrupted last attempt is never due again (NextAttemptAt {pending.NextAttemptAt:O})");
        }

        // Once the lease ran out, the retry job resumes the last attempt and delivers.
        await MakeDueAsync(rp.ClientId);
        Sink.Hang(rp.LogoutUri!, hang: false);
        Sink.Respond(rp.LogoutUri!, HttpStatusCode.OK);
        using (var scope = CreateTenantScope())
            Assert.Equal((1, 1), await scope.ServiceProvider.GetRequiredService<IBackChannelLogoutDeliverer>().SweepDueAsync(ct));
        Assert.Equal(3, (await Sink.WaitForAsync(rp.LogoutUri!, 3)).Count);
        await using (var query = GetTenantedSession())
            Assert.Empty(await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).ToListAsync(ct));
    }

    [Fact]
    public async Task A_last_attempt_interrupted_too_often_is_given_up_and_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var rp = await LogOutWithFailingRelyingPartyAsync("lastgone");
        await using (var mutate = GetTenantedDocumentSession())
        {
            var pending = await mutate.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).SingleAsync(ct);
            pending.Attempts = BackChannelLogoutConstants.RetrySchedule.Length + 1
                + BackChannelLogoutConstants.InterruptedLastAttemptRetries;
            pending.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            mutate.Store(pending);
            await mutate.SaveChangesAsync(ct);
        }

        using (var scope = CreateTenantScope())
            Assert.Equal((1, 0), await scope.ServiceProvider.GetRequiredService<IBackChannelLogoutDeliverer>().SweepDueAsync(ct));

        // No further POST; the row is gone and the outcome says why.
        Assert.Single(Sink.Deliveries.Where(d => d.Target.ToString() == rp.LogoutUri));
        await using (var query = GetTenantedSession())
            Assert.Empty(await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).ToListAsync(ct));
        var detail = await WaitForDeliveryStatusAsync(rp.Id, expected: BackChannelLogoutConstants.InterruptedOutcome);
        Assert.Equal(BackChannelLogoutConstants.InterruptedOutcome, detail!.BackChannelLogoutLastOutcome);
    }

    private async Task MakeDueAsync(string clientId)
    {
        await using var mutate = GetTenantedDocumentSession();
        var pending = await mutate.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == clientId)
            .SingleAsync(TestContext.Current.CancellationToken);
        pending.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        mutate.Store(pending);
        await mutate.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_dispatcher_does_not_resend_a_delivery_before_it_is_due()
    {
        var ct = TestContext.Current.CancellationToken;
        var rp = await LogOutWithFailingRelyingPartyAsync("notdue");
        Guid id;
        await using (var query = GetTenantedSession())
        {
            var pending = await query.Query<BackChannelLogoutDelivery>().Where(d => d.ClientId == rp.ClientId).SingleAsync(ct);
            Assert.Equal(1, pending.Attempts);
            Assert.True(pending.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(30));
            id = pending.Id;
        }

        // A stale queue entry for the same delivery reaches the dispatcher now.
        Factory.Services.GetRequiredService<BackChannelLogoutDispatchQueue>().Enqueue("system", id);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        Assert.Single(Sink.Deliveries.Where(d => d.Target.ToString() == rp.LogoutUri));
        await using (var query = GetTenantedSession())
            Assert.Equal(1, (await query.LoadAsync<BackChannelLogoutDelivery>(id, ct))!.Attempts);
    }

    /// <summary>A relying party whose logout URI answers 502, a signed-in user with its
    /// tokens, and a logout: the immediate attempt fails and leaves one pending row.</summary>
    private async Task<RelyingParty> LogOutWithFailingRelyingPartyAsync(string tag)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, userName) = await CreateUserAsync(tag);
        var rp = await CreateRelyingPartyAsync(tag, AccessTokenType.Jwt, logoutUri: SinkUri(tag));
        Sink.Respond(rp.LogoutUri!, HttpStatusCode.BadGateway);

        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        (await cookieClient.PostAsJsonAsync("/api/account/logout", new { }, ct)).EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();
        Assert.Single(await Sink.WaitForAsync(rp.LogoutUri!, 1));
        await WaitForDeliveryStatusAsync(rp.Id);
        return rp;
    }
}
