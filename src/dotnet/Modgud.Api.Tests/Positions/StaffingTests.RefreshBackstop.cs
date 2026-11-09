using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// G-B4-04 — the staffing refresh is the backstop when a cascade was missed or raced. It
/// checked the position's existence and terminal policy but not whether the position is
/// active, nor a staffing lifetime the policy shortened since the tap; and every access token
/// lived ten minutes, also past the shift's end.
/// </summary>
public partial class StaffingTests
{
    [Fact]
    public async Task Refresh_ends_the_shift_of_a_deactivated_position()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-inactive", ct);
        var refreshToken = await TapForRefreshTokenAsync(setup, ct);

        // A missed cascade: the position went inactive, the session is still Active.
        await MutatePositionAsync(setup.PositionId, p => p.IsActive = false, ct);

        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        Assert.False(refresh.IsSuccessStatusCode, "an inactive position must not refresh its shift");
        Assert.Contains("staffing_required", await refresh.Content.ReadAsStringAsync(ct));
        Assert.Equal(StaffingSessionEndReason.PositionDisabled, (await LoadStaffingAsync(setup, ct)).EndReason);
    }

    [Fact]
    public async Task Refresh_ends_a_shift_the_policy_has_since_shortened_below_its_age()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-shorter", ct);
        var refreshToken = await TapForRefreshTokenAsync(setup, ct);

        // The shift started two hours ago; the policy now allows one hour at most.
        await MutateStaffingAsync(setup, s => s.StartedAt = DateTimeOffset.UtcNow.AddHours(-2), ct);
        await MutatePositionAsync(setup.PositionId, p => p.TerminalPolicy = p.TerminalPolicy with
        {
            StaffingSessionLifetime = TimeSpan.FromHours(1),
            MaximumStaffingSessionLifetime = TimeSpan.FromHours(1),
        }, ct);

        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        Assert.False(refresh.IsSuccessStatusCode, "a shift older than the current lifetime must not refresh");
        Assert.Contains("staffing_required", await refresh.Content.ReadAsStringAsync(ct));
        Assert.Equal(StaffingSessionEndReason.PolicyTightened, (await LoadStaffingAsync(setup, ct)).EndReason);
    }

    [Fact]
    public async Task A_refreshed_access_token_does_not_outlive_the_shift()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-clamp", ct);
        var refreshToken = await TapForRefreshTokenAsync(setup, ct);

        await MutateStaffingAsync(setup, s => s.AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3), ct);

        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        var body = await refresh.Content.ReadAsStringAsync(ct);
        Assert.True(refresh.IsSuccessStatusCode, $"staffing refresh failed ({(int)refresh.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();
        Assert.True(expiresIn <= 180, $"the access token must end with the shift (3 min), expires_in = {expiresIn}");
    }

    private async Task<string> TapForRefreshTokenAsync(StaffingSetup setup, CancellationToken ct)
    {
        using var tokens = await TapAsync(setup, ct);
        return tokens.RootElement.GetProperty("refresh_token").GetString()!;
    }

    private async Task MutatePositionAsync(Guid positionId, Action<PositionPrincipal> change, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var position = (await docs.LoadAsync<PositionPrincipal>(positionId, ct))!;
        change(position);
        docs.Store(position);
        await docs.SaveChangesAsync(ct);
    }

    private async Task MutateStaffingAsync(StaffingSetup setup, Action<StaffingSession> change, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var staffing = (await docs.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
        change(staffing);
        docs.Store(staffing);
        await docs.SaveChangesAsync(ct);
    }

    private async Task<StaffingSession> LoadStaffingAsync(StaffingSetup setup, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
    }
}
