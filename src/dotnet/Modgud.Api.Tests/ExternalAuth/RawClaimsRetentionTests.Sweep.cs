using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>EP-03 — the realm's retention sweep (the storage half of the contract).</summary>
public partial class RawClaimsRetentionTests
{
    [Fact]
    public async Task The_sweep_removes_expired_raw_claims_from_links_and_login_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var limited = await CreateProviderAsync(retentionDays: 1);
        var unlimited = await CreateProviderAsync(retentionDays: null);
        var expired = await SeedLinkAsync(Guid.NewGuid(), limited.Id, Now.AddDays(-3));
        // Logged in again recently: its older login event is past the retention, the
        // latest one is not.
        var recent = await SeedLinkAsync(Guid.NewGuid(), limited.Id, Now.AddDays(-3), Now.AddHours(-1));
        var kept = await SeedLinkAsync(Guid.NewGuid(), unlimited.Id, Now.AddDays(-30));

        Assert.Equal(new RawClaimsSweepResult(1, 2), await SweepAsync(ct));
        // Idempotent: what is cleared is not found again.
        Assert.Equal(new RawClaimsSweepResult(0, 0), await SweepAsync(ct));

        await using var query = GetTenantedSession();
        Assert.False(HasRawClaims(await query.LoadAsync<ExternalIdentityLink>(expired, ct)));
        Assert.True(HasRawClaims(await query.LoadAsync<ExternalIdentityLink>(recent, ct)));
        Assert.True(HasRawClaims(await query.LoadAsync<ExternalIdentityLink>(kept, ct)));

        var expiredEvents = await LoginEventsAsync(query, expired);
        var only = Assert.Single(expiredEvents);
        Assert.Equal(JsonValueKind.Null, only.GetProperty("RawClaims").ValueKind);
        Assert.Equal("person@idp.test", only.GetProperty("Email").GetString());
        Assert.Equal(JsonValueKind.Object, only.GetProperty("ScriptOutput").ValueKind);

        var recentEvents = await LoginEventsAsync(query, recent);
        Assert.Equal(JsonValueKind.Null, recentEvents[0].GetProperty("RawClaims").ValueKind);
        Assert.Equal(JsonValueKind.Object, recentEvents[1].GetProperty("RawClaims").ValueKind);

        Assert.Equal(JsonValueKind.Object, Assert.Single(await LoginEventsAsync(query, kept)).GetProperty("RawClaims").ValueKind);
    }

    private async Task<RawClaimsSweepResult> SweepAsync(CancellationToken ct)
    {
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RawClaimsRetentionSweeper>().SweepAsync(ct);
    }
}
