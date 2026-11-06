using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.PositionTerminals;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// G-B4-02 — ending a staffing session committed Ended + the terminal-pointer clear first and
/// revoked the OAuth authorization afterwards. When that revoke failed, the session stayed
/// Ended with a live authorization, and nothing (a repeated end, the sweep, the per-subject
/// cascades) picked it up again, because they all select Active sessions only. The revoke is
/// now a follow-up written in the same commit and retried until it succeeds; an activation
/// token's disable/revoke runs its cascade on every call, so a repeat finishes an interrupted one.
/// </summary>
public partial class StaffingTests
{
    [Fact]
    public async Task A_failing_authorization_revoke_after_the_session_ended_is_completed_by_the_follow_up()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var staffing = await StartStaffingSessionAsync("fu-revoke", ct);
        Assert.True(await IsAuthorizationValidAsync(staffing.OAuthAuthorizationId, ct));

        Factory.GrantRevokerFaults.FailAuthorizationRevokes = 2; // the immediate attempt and the first follow-up attempt
        using (var scope = Factory.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<IStaffingRevoker>();
            try { await revoker.EndSessionAsync(staffing.Id, StaffingSessionEndReason.RemoteLock, ct); }
            catch (InvalidOperationException) { /* the pre-fix behaviour: the request failed after the commit */ }
        }

        // A repeated end finds no Active session any more — before the fix nothing revoked it.
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IStaffingRevoker>().EndSessionAsync(staffing.Id, StaffingSessionEndReason.RemoteLock, ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (await IsAuthorizationValidAsync(staffing.OAuthAuthorizationId, ct))
        {
            Assert.True(DateTime.UtcNow < deadline, "The staffing session's authorization is still valid.");
            await Task.Delay(200, ct);
        }
        // Both injected failures were hit: the follow-up failed once and its retry revoked.
        Assert.Equal(0, Factory.GrantRevokerFaults.FailAuthorizationRevokes);
    }

    [Fact]
    public async Task Disabling_an_activation_token_again_finishes_an_interrupted_cascade()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var staffing = await StartStaffingSessionAsync("fu-token", ct);

        // State after an interrupted disable: the token is already Disabled, but a session
        // activated with it is still Active.
        var tokenId = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            docs.Store(new ActivationToken
            {
                Id = tokenId, Label = "fu-token", Status = ActivationTokenStatus.Disabled,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var stored = await docs.LoadAsync<StaffingSession>(staffing.Id, ct);
            stored!.Evidence = stored.Evidence with { ActivationTokenId = tokenId };
            docs.Store(stored);
            await docs.SaveChangesAsync(ct);
        }

        var disable = await Client.PostAsync($"/api/activation-token/{tokenId}/disable", null, ct);
        Assert.True(disable.IsSuccessStatusCode, await disable.Content.ReadAsStringAsync(ct));

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var after = await session.LoadAsync<StaffingSession>(staffing.Id, ct);
            Assert.Equal(StaffingSessionStatus.Ended, after!.Status);
        }
    }

    private async Task<StaffingSession> StartStaffingSessionAsync(string prefix, CancellationToken ct)
    {
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync(prefix, ct);
        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");
        var redeemed = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        Assert.True(redeemed.IsSuccessStatusCode, await redeemed.Content.ReadAsStringAsync(ct));
        var staffing = await LoadStaffingAsync(setup, ct);
        Assert.Equal(StaffingSessionStatus.Active, staffing.Status);
        Assert.False(string.IsNullOrEmpty(staffing.OAuthAuthorizationId));
        return staffing;
    }

    private async Task<bool> IsAuthorizationValidAsync(string authorizationId, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await manager.FindByIdAsync(authorizationId, ct);
        return authorization is not null
            && await manager.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Valid, ct);
    }
}
