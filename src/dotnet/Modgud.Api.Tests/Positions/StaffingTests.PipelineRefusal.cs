using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.OpenIddict.Dpop;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// G-B4-03 — redeeming a staffing ceremony committed the new session, moved the terminal pointer and
/// ended (and revoked) the previous session before the token pipeline ran its DPoP checks. A proof the
/// pipeline then refused (here: a jti already used) left the caller without tokens, the new session
/// unusable and the running shift ended. The refusal now happens before anything is stored.
/// </summary>
public partial class StaffingTests
{
    [Fact]
    public async Task A_redeem_the_dpop_pipeline_refuses_leaves_the_running_session_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-refuse", ct);
        Assert.Equal(DeviceBindingIds.Dpop, setup.Binding);
        using (await TapAsync(setup, ct)) { }
        var running = await LoadStaffingAsync(setup, ct);
        Assert.Equal(StaffingSessionStatus.Active, running.Status);

        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount: 2);
        var used = setup.DeviceKey.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow);
        await TerminalDeviceEnrollmentTests.MarkProofUsedAsync(Factory.Services, used, ct);
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(StaffingForm(setup.ClientId, begin.CeremonyId, assertion)),
        };
        request.Headers.Add(DpopConstants.HeaderName, used);
        var refused = await Factory.CreateClient().SendAsync(request, ct);
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("already been used", await refused.Content.ReadAsStringAsync(ct));

        using var scope = Factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var sessions = await query.Query<StaffingSession>()
            .Where(x => x.TerminalEnrollmentId == running.TerminalEnrollmentId).ToListAsync(ct);
        Assert.True(sessions.Count == 1, $"the refused redeem stored {sessions.Count - 1} more staffing session(s)");
        Assert.Equal(StaffingSessionStatus.Active, Assert.Single(sessions).Status);
    }
}
